using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Backend.Auth;
using Backend.Models.Users;
using Backend.Models.Auth;
using Backend.Models.Messages;
using Backend.Services.Auth;
using Backend.Services.Messages;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pooshit.Ocelot.Entities;
using Backend.tests.Fixtures;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Pooshit.Json;

namespace Backend.tests.Tests;

/// <summary>Exercises Connector authentication, boundary rejection, SDK replies and DiVoid isolation.</summary>
[TestFixture, Parallelizable]
public class TeamsWebhookTests {
    /// <summary>Checks that the native SDK sends the fixed setup reply.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Test, Parallelizable]
    public async Task ProcessAsync_ValidConnectorText_SendsFixedSdkReply() {
        using TeamsWebhookFixture fixture = new();

        using HttpResponseMessage response = await fixture.Post(fixture.Token());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(fixture.Metadata.KeyRequests, Is.GreaterThan(0));
        Assert.That(fixture.Connector.Bodies, Has.Count.EqualTo(1));
        Dictionary<string, object> reply = Json.Read<Dictionary<string, object>>(fixture.Connector.Bodies.Single());
        Assert.That(reply["type"], Is.EqualTo("message"));
        Assert.That(reply["text"], Is.EqualTo("DiVoid Teams webhook is connected. Message routing to DiVoid users is not implemented yet."));
        Assert.That(fixture.Connector.Destinations.Single().AbsoluteUri,
            Does.StartWith(TeamsWebhookFixture.ServiceUrl).And.Contain("conversation-1"));
        Assert.That(fixture.Database.Invocations, Is.Empty);
        Assert.That(fixture.Messages.Invocations, Is.Empty);
    }

    /// <summary>Checks that rejected input produces no outbound send.</summary>
    /// <param name="failure">The invalid input scenario.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase("missing"), TestCase("malformed"), TestCase("signature"), TestCase("unknown-key")]
    [TestCase("issuer"), TestCase("audience"), TestCase("audience-slash"), TestCase("expired"), TestCase("future")]
    [TestCase("entra"), TestCase("keycloak"), TestCase("api-key"), TestCase("algorithm")]
    [Parallelizable]
    public async Task ProcessAsync_InvalidAuthentication_RejectsBeforeSend(string failure) {
        using TeamsWebhookFixture fixture = new();
        using RSA wrong = RSA.Create(2048);
        string? token = failure switch {
            "missing" => null,
            "malformed" => "invalid-token",
            "signature" => fixture.Token(key: new RsaSecurityKey(wrong) { KeyId = fixture.Key.KeyId }),
            "unknown-key" => fixture.Token(key: new RsaSecurityKey(wrong) { KeyId = "unknown" }),
            "issuer" => fixture.Token(issuer: "https://unexpected.invalid"),
            "audience" => fixture.Token(audience: "wrong-bot"),
            "audience-slash" => fixture.Token(audience: TeamsWebhookFixture.ClientId + "/"),
            "expired" => fixture.Token(notBefore: DateTime.UtcNow.AddHours(-1), expires: DateTime.UtcNow.AddMinutes(-10)),
            "future" => fixture.Token(notBefore: DateTime.UtcNow.AddMinutes(10), expires: DateTime.UtcNow.AddMinutes(20)),
            "entra" => fixture.Token(issuer: "https://login.microsoftonline.com/" + TeamsWebhookFixture.TenantId + "/v2.0"),
            "keycloak" => fixture.Token(issuer: "https://keycloak.invalid/realm", audience: "divoid-tests"),
            "api-key" => "123.test-api-key",
            "algorithm" => fixture.Token(algorithm: SecurityAlgorithms.RsaSha512),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };

        using HttpResponseMessage response = await fixture.Post(token);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), failure);
        Assert.That(fixture.Connector.Bodies, Is.Empty);
        Assert.That(fixture.Database.Invocations, Is.Empty);
    }

    /// <summary>Checks Connector authentication when DiVoid authentication is disabled.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Test, Parallelizable]
    public async Task ProcessAsync_DivoidAuthenticationDisabled_StillRequiresConnectorToken() {
        using TeamsWebhookFixture fixture = new(authEnabled: false);

        using HttpResponseMessage rejected = await fixture.Post(null);
        using HttpResponseMessage accepted = await fixture.Post(fixture.Token());

        Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(fixture.Connector.Bodies, Has.Count.EqualTo(1));
    }

    /// <summary>Checks that rejected input produces no outbound send.</summary>
    /// <param name="failure">The invalid input scenario.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase("channel"), TestCase("tenant-missing"), TestCase("tenant-conflict"), TestCase("conversation-tenant")]
    [TestCase("http"), TestCase("query"), TestCase("fragment"), TestCase("userinfo")]
    [TestCase("claim-missing"), TestCase("claim-empty"), TestCase("claim-mismatch"), TestCase("claim-conflict"), TestCase("claim-case")]
    [Parallelizable]
    public async Task ProcessAsync_InvalidActivityBoundary_RejectsBeforeSend(string failure) {
        using TeamsWebhookFixture fixture = new();
        Dictionary<string, object?> activity = TeamsWebhookFixture.Activity();
        Claim[] claims = [new("serviceUrl", TeamsWebhookFixture.ServiceUrl)];
        switch (failure) {
            case "channel": activity["channelId"] = "webchat"; break;
            case "tenant-missing": activity.Remove("channelData"); break;
            case "tenant-conflict": activity["channelData"] = new Dictionary<string, object> {
                ["tenant"] = new Dictionary<string, object> { ["id"] = Guid.NewGuid().ToString() }
            }; break;
            case "conversation-tenant": activity["conversation"] = new Dictionary<string, object> {
                ["id"] = "conversation-1", ["tenantId"] = Guid.NewGuid().ToString()
            }; break;
            case "http": activity["serviceUrl"] = "http://smba.trafficmanager.net/emea/"; break;
            case "query": activity["serviceUrl"] = TeamsWebhookFixture.ServiceUrl + "?query=value"; break;
            case "fragment": activity["serviceUrl"] = TeamsWebhookFixture.ServiceUrl + "#fragment"; break;
            case "userinfo": activity["serviceUrl"] = "https://user@smba.trafficmanager.net/emea/"; break;
            case "claim-missing": claims = []; break;
            case "claim-empty": claims = [new("serviceUrl", "")]; break;
            case "claim-mismatch": claims = [new("serviceUrl", "https://smba.trafficmanager.net/amer/")]; break;
            case "claim-conflict": claims = [new("serviceUrl", TeamsWebhookFixture.ServiceUrl), new("serviceurl", "https://smba.trafficmanager.net/amer/")]; break;
            case "claim-case": claims = [new("SERVICEURL", TeamsWebhookFixture.ServiceUrl)]; break;
        }

        using HttpResponseMessage response = await fixture.Post(fixture.Token(claims: claims), activity);

        Assert.That(fixture.Metadata.KeyRequests, Is.GreaterThan(0));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), failure);
        Assert.That(fixture.Connector.Bodies, Is.Empty);
        Assert.That(fixture.Database.Invocations, Is.Empty);
    }

    /// <summary>Checks the supported service URL claim spellings.</summary>
    /// <param name="includeCamelCase">Whether both accepted claim spellings are present.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task ProcessAsync_LowercaseServiceUrlClaim_AcceptsExactBinding(bool includeCamelCase) {
        using TeamsWebhookFixture fixture = new();
        List<Claim> claims = [new("serviceurl", TeamsWebhookFixture.ServiceUrl)];
        if (includeCamelCase)
            claims.Add(new("serviceUrl", TeamsWebhookFixture.ServiceUrl));

        using HttpResponseMessage response = await fixture.Post(fixture.Token(claims: claims.ToArray()));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(fixture.Connector.Bodies, Has.Count.EqualTo(1));
    }

    /// <summary>Checks that rejected input produces no outbound send.</summary>
    /// <param name="endorsement">The invalid signing key endorsement scenario.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase("missing"), TestCase("wrong"), Parallelizable]
    public async Task ProcessAsync_UnendorsedSigningKey_RejectsBeforeSend(string endorsement) {
        using TeamsWebhookFixture fixture = new();
        fixture.Metadata.Endorsements = endorsement == "missing" ? null : ["webchat"];

        using HttpResponseMessage response = await fixture.Post(fixture.Token());

        Assert.That(fixture.Metadata.KeyRequests, Is.GreaterThan(0));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(fixture.Connector.Bodies, Is.Empty);
    }

    /// <summary>Checks authoritative signing key rotation.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Test, Parallelizable]
    public async Task ProcessAsync_AuthoritativeKeyRotation_AcceptsRefreshedKey() {
        using TeamsWebhookFixture fixture = new();
        using HttpResponseMessage first = await fixture.Post(fixture.Token());
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using RSA rotatedRsa = RSA.Create(2048);
        RsaSecurityKey rotated = new(rotatedRsa) { KeyId = "teams-rotated" };
        fixture.Metadata.Key = rotated;
        JwtBearerOptions options = fixture.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get("TeamsBot");
        ConfigurationManager<OpenIdConnectConfiguration> manager = (ConfigurationManager<OpenIdConnectConfiguration>)options.ConfigurationManager!;
        manager.RefreshInterval = TimeSpan.FromSeconds(1);
        manager.RequestRefresh();
        for (int attempt = 0; attempt < 100; attempt++) {
            OpenIdConnectConfiguration current = await manager.GetConfigurationAsync(CancellationToken.None);
            if (current.SigningKeys.Any(key => key.KeyId == rotated.KeyId))
                break;
            await Task.Delay(10);
        }

        using HttpResponseMessage second = await fixture.Post(fixture.Token(key: rotated));

        Assert.That(fixture.Metadata.KeyRequests, Is.GreaterThanOrEqualTo(2));
        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(fixture.Connector.Bodies, Has.Count.EqualTo(2));
    }

    /// <summary>Checks malformed activity rejection.</summary>
    /// <param name="failure">The invalid input scenario.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase("json"), TestCase("type"), TestCase("id"), TestCase("conversation"), TestCase("from"), TestCase("recipient")]
    [Parallelizable]
    public async Task ProcessAsync_MalformedActivity_ReturnsBadRequest(string failure) {
        using TeamsWebhookFixture fixture = new();
        Dictionary<string, object?> activity = TeamsWebhookFixture.Activity();
        if (failure != "json")
            activity.Remove(failure);
        string json = failure == "json" ? "{invalid" : Json.WriteString(activity);

        using HttpResponseMessage response = await fixture.PostBody(fixture.Token(), new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.That(fixture.Metadata.KeyRequests, Is.GreaterThan(0));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), failure);
        Assert.That(fixture.Connector.Bodies, Is.Empty);
    }

    /// <summary>Checks strict UTF-8 and native JSON nesting bounds.</summary>
    /// <param name="invalidUtf8">Whether the payload contains invalid UTF-8.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task ProcessAsync_DeepJsonOrInvalidUtf8_ReturnsBadRequest(bool invalidUtf8) {
        using TeamsWebhookFixture fixture = new();
        byte[] bytes = invalidUtf8 ? [0xff, 0xfe, 0xfd] :
            Encoding.UTF8.GetBytes("{\"nested\":" + new string('[', 100) + "0" + new string(']', 100) + "}");
        using ByteArrayContent content = new(bytes);
        content.Headers.ContentType = new("application/json");

        using HttpResponseMessage response = await fixture.PostBody(fixture.Token(), content);

        Assert.That(fixture.Metadata.KeyRequests, Is.GreaterThan(0));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(fixture.Connector.Bodies, Is.Empty);
    }

    /// <summary>Checks known-length and unknown-length body bounds.</summary>
    /// <param name="unknownLength">Whether the body has an unknown content length.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task ProcessAsync_OversizedBody_ReturnsPayloadTooLarge(bool unknownLength) {
        using TeamsWebhookFixture fixture = new();
        string body = new('x', 256 * 1024 + 1);
        using HttpContent content = unknownLength ? new TeamsChunkedContent(body) : new StringContent(body);
        Assert.That(content.Headers.ContentLength, unknownLength ? Is.Null : Is.Not.Null);

        using HttpResponseMessage response = await fixture.PostBody(fixture.Token(), content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        Assert.That(fixture.Connector.Bodies, Is.Empty);
    }

    /// <summary>Checks acknowledgment and unsupported activity outcomes.</summary>
    /// <param name="type">The activity type scenario.</param>
    /// <param name="status">The expected HTTP status.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase("conversationUpdate", 200), TestCase("invoke", 501), TestCase("attachment", 200)]
    [Parallelizable]
    public async Task ProcessAsync_NontextOrUnsupportedActivity_AcknowledgesWithoutReply(string type, int status) {
        using TeamsWebhookFixture fixture = new();
        Dictionary<string, object?> activity = TeamsWebhookFixture.Activity();
        activity["type"] = type == "attachment" ? "message" : type;
        activity.Remove("text");

        using HttpResponseMessage response = await fixture.Post(fixture.Token(), activity);

        Assert.That(response.StatusCode, Is.EqualTo((HttpStatusCode)status));
        Assert.That(fixture.Connector.Bodies, Is.Empty);
        Assert.That(fixture.Messages.Invocations, Is.Empty);
    }

    /// <summary>Checks propagation of SDK send failures.</summary>
    /// <param name="cancel">Whether the transport reports cancellation.</param>
    /// <param name="status">The expected HTTP status.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(false, 502), TestCase(true, 504), Parallelizable]
    public async Task ProcessAsync_OutboundFailure_DoesNotReportSuccess(bool cancel, int status) {
        using TeamsWebhookFixture fixture = new();
        fixture.Connector.Status = HttpStatusCode.BadGateway;
        fixture.Connector.CancelSend = cancel;

        using HttpResponseMessage response = await fixture.Post(fixture.Token());

        Assert.That(fixture.Connector.Bodies, Has.Count.EqualTo(1));
        Assert.That(response.StatusCode, Is.EqualTo((HttpStatusCode)status));
    }

    /// <summary>Checks payload and credential privacy with trace logging enabled.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Test, Parallelizable]
    public async Task ProcessAsync_TraceLogging_DoesNotExposePayloadOrCredentials() {
        TeamsLogCapture capture = new();
        using TeamsWebhookFixture fixture = new(configureServices: services => {
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(capture));
            services.PostConfigure<LoggerFilterOptions>(options => {
                foreach (LoggerFilterRule rule in options.Rules.Where(rule => rule.CategoryName != "Microsoft.Teams").ToArray())
                    options.Rules.Remove(rule);
                options.MinLevel = LogLevel.Trace;
            });
        });
        fixture.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Teams.Trace.Control")
            .LogTrace("teams-trace-control");
        string token = fixture.Token();
        Dictionary<string, object?> activity = TeamsWebhookFixture.Activity();

        using HttpResponseMessage success = await fixture.Post(token, activity);
        fixture.Connector.Status = HttpStatusCode.BadGateway;
        using HttpResponseMessage failure = await fixture.Post(token, activity);

        Assert.That(success.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(failure.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
        Assert.That(fixture.Connector.Bodies, Has.Count.EqualTo(2));
        Assert.That(capture.Messages.Any(message => message.Contains("teams-trace-control")), Is.True);
        Assert.That(capture.Messages.Any(message => message.Contains((string)activity["text"]!) ||
            message.Contains("synthetic-fixture-secret") || message.Contains(token)), Is.False);
    }

    /// <summary>Checks the webhook contract.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Test, Parallelizable]
    public async Task AddTeamsWebhook_Enabled_PreservesDivoidAuthenticationDefaults() {
        using TeamsWebhookFixture fixture = new();
        AuthenticationOptions authentication = fixture.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
        AuthorizationOptions authorization = fixture.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        ClaimsPrincipal principal = new(new ClaimsIdentity([
            new("iss", "https://api.botframework.com"), new("userId", "1")
        ], "TeamsBot"));

        ClaimsPrincipal result = await fixture.Services.GetRequiredService<IClaimsTransformation>().TransformAsync(principal);
        using HttpResponseMessage nodes = await fixture.PostBody(fixture.Token(), new StringContent("{}"), "api/nodes");
        using HttpResponseMessage messages = await fixture.PostBody(fixture.Token(), new StringContent("{}"), "api/messages");

        Assert.That(authentication.DefaultScheme, Is.EqualTo("DiVoidBearer"));
        Assert.That(authorization.DefaultPolicy.AuthenticationSchemes, Is.Empty);
        Assert.That(authorization.FallbackPolicy, Is.Not.Null);
        Assert.That(authorization.GetPolicy("TeamsBot")!.AuthenticationSchemes, Is.EqualTo(new[] { "TeamsBot" }));
        Assert.That(result.HasClaim(claim => claim.Type is "divoid.user_id" or "permission"), Is.False);
        Assert.That(fixture.Database.Invocations, Is.Empty);
        Assert.That(nodes.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(messages.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    /// <summary>Checks existing credentials and message author binding.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Test, Parallelizable]
    public async Task Create_TeamsEnabled_PreservesKeycloakAndApiKeyCallerIdentity() {
        using DatabaseFixture database = new();
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey key = new(rsa) { KeyId = "keycloak-test" };
        using TeamsWebhookFixture fixture = new(configureServices: services => {
            services.RemoveAll<IEntityManager>();
            services.AddSingleton(database.EntityManager);
            services.RemoveAll<IMessageService>();
            services.AddTransient<IMessageService, MessageService>();
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options => {
                options.Configuration = new OpenIdConnectConfiguration {
                    Issuer = "https://keycloak.invalid/realm"
                };
                options.Configuration.SigningKeys.Add(key);
            });
        });
        long author = await database.EntityManager.Insert<User>()
            .Columns(user => user.Name, user => user.Enabled, user => user.Permissions, user => user.CreatedAt)
            .Values("teams-auth-author", true, Json.WriteString(new[] { "read", "write" }), DateTime.UtcNow)
            .ReturnID().ExecuteAsync();
        long recipient = await database.EntityManager.Insert<User>()
            .Columns(user => user.Name, user => user.Enabled, user => user.Permissions, user => user.CreatedAt)
            .Values("teams-auth-recipient", true, Json.WriteString(new[] { "read" }), DateTime.UtcNow)
            .ReturnID().ExecuteAsync();
        string keycloak = fixture.Token(issuer: "https://keycloak.invalid/realm", audience: "divoid-tests", key: key,
            claims: [new("userId", author.ToString())]);
        ApiKeyDetails apiKey = await fixture.Services.GetRequiredService<IApiKeyService>().CreateApiKey(new ApiKeyParameters {
            UserId = author, Permissions = ["read", "write"]
        });
        MessageDetails payload = new() { RecipientId = recipient, Subject = "setup isolation", Body = "caller preserved", AuthorId = recipient };

        using HttpResponseMessage jwtResponse = await fixture.PostBody(keycloak,
            new StringContent(Json.WriteString(payload), Encoding.UTF8, "application/json"), "api/messages");
        using HttpResponseMessage apiResponse = await fixture.PostBody(apiKey.PlaintextKey,
            new StringContent(Json.WriteString(payload), Encoding.UTF8, "application/json"), "api/messages");
        using HttpResponseMessage webhook = await fixture.Post(fixture.Token(claims: [
            new("serviceUrl", TeamsWebhookFixture.ServiceUrl), new("userId", author.ToString())
        ]));

        Assert.That(jwtResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(apiResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        MessageDetails jwtMessage = Json.Read<MessageDetails>(await jwtResponse.Content.ReadAsStringAsync());
        MessageDetails apiMessage = Json.Read<MessageDetails>(await apiResponse.Content.ReadAsStringAsync());
        Assert.That(jwtMessage.AuthorId, Is.EqualTo(author));
        Assert.That(apiMessage.AuthorId, Is.EqualTo(author));
        Assert.That(jwtMessage.Id, Is.GreaterThan(0));
        Assert.That(apiMessage.Id, Is.GreaterThan(jwtMessage.Id));
        Assert.That(webhook.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(fixture.Connector.Bodies, Has.Count.EqualTo(1));
    }

    /// <summary>Checks the disabled webhook route.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Test, Parallelizable]
    public async Task MapTeamsWebhook_Disabled_ReturnsNotFound() {
        using TeamsWebhookFixture fixture = new(authEnabled: false, overrides: new() { ["Teams:Enabled"] = "false" });

        using HttpResponseMessage response = await fixture.Post(null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(fixture.Connector.Bodies, Is.Empty);
    }

    /// <summary>Checks fail-closed configuration validation.</summary>
    /// <param name="field">The invalid configuration field.</param>
    /// <param name="value">The invalid configuration value.</param>
    [TestCase("AzureAd:ClientId", "invalid"), TestCase("AzureAd:TenantId", "")]
    [TestCase("AzureAd:ClientCredentials:0:ClientSecret", "")]
    [TestCase("AzureAd:ClientCredentials:0:SourceType", "ManagedIdentity")]
    [TestCase("AzureAd:Instance", "https://login.microsoftonline.us/")]
    [TestCase("BotFramework:OpenIdMetadataUrl", "https://untrusted.invalid/")]
    [TestCase("BotFramework:BotTokenIssuer", "https://untrusted.invalid/")]
    [TestCase("AzureAd:Authority", "https://untrusted.invalid/")]
    [TestCase("AzureAd:InstanceDiscoveryMetadata", "untrusted")]
    [TestCase("AzureAd:DangerouslyAllowUnauthenticatedRequests", "invalid")]
    [TestCase("AzureAd:DangerouslyAllowUnauthenticatedRequests", "true")]
    [TestCase("Teams:DangerouslyAllowUnauthenticatedRequests", "true")]
    [Parallelizable]
    public void AddTeamsWebhook_IncompleteOrUnsafeConfiguration_FailsStartup(string field, string value) {
        InvalidOperationException? exception = Assert.Throws<InvalidOperationException>(() => {
            using TeamsWebhookFixture fixture = new(overrides: new() { [field] = value });
        });

        Assert.That(exception!.Message, Does.Contain("Teams").Or.Contain("AzureAd"));
        Assert.That(exception.Message, Does.Not.Contain("synthetic-fixture-secret"));
    }
}
