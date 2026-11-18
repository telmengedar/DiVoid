using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using Backend.Models.Auth;
using Backend.Models.Messages;
using Backend.Services.Auth;
using Backend.tests.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Abstractions;
using Moq;
using Pooshit.Http.Paths;
using Pooshit.Json;
using Pooshit.Ocelot.Entities;

namespace Backend.tests.Tests;

/// <summary>Checks actual native diagnostic request observations and safe response challenge classification.</summary>
[TestFixture, Parallelizable]
public class TeamsDiagnosisObservationTests {
    /// <summary>Checks a live authenticated API request observes its native app token without forwarding the caller credential.</summary>
    /// <param name="apiKey">Whether the caller uses the API-key authentication branch.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task Diagnose_LiveCaller_ObservesNativeAppHeaderAndPreservesRoute(bool apiKey) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        string token = Token("matching");
        Mock<IAuthorizationHeaderProvider> provider = Provider("Bearer " + token);
        TeamsLogCapture capture = new();
        using TeamsRoutingFixture fixture = Fixture(database, provider, capture);
        string caller = apiKey ? (await fixture.Host.Services.GetRequiredService<IApiKeyService>().CreateApiKey(new ApiKeyParameters {
            UserId = database.RecipientId, Permissions = ["read", "write"]
        })).PlaintextKey : fixture.UserToken(database.RecipientId);
        bool liveCallerObserved = false;
        provider.Setup(auth => auth.CreateAuthorizationHeaderForAppAsync(It.IsAny<string>(),
                It.IsAny<AuthorizationHeaderProviderOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() => liveCallerObserved = fixture.Host.Services.GetRequiredService<IHttpContextAccessor>()
                .HttpContext!.Request.Headers.Authorization.ToString() == "Bearer " + caller).ReturnsAsync("Bearer " + token);
        long id = await Receive(fixture, "private:conversation/with+space ?");
        TeamsMessageOrigin before = await database.Entities.Load<TeamsMessageOrigin>().ExecuteEntityAsync();
        fixture.Host.Connector.Status = HttpStatusCode.Unauthorized;
        fixture.Host.Connector.ResponseBody = "{\"message\":\"Authorization has been denied for this request.\"}";
        fixture.Host.Connector.AuthenticationChallenges = ["Bearer"];

        using HttpResponseMessage response = await fixture.Host.Send(HttpMethod.Post,
            Rest.Path("api/messages", id, "reply", "diagnosis"), caller);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(liveCallerObserved, Is.True);
        provider.Verify(auth => auth.CreateAuthorizationHeaderForAppAsync("https://api.botframework.com/.default",
            It.Is<AuthorizationHeaderProviderOptions>(options => options.AcquireTokenOptions.AuthenticationOptionsName == "AzureAd"),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.That(fixture.Host.Connector.Methods.ToArray(), Is.EqualTo(new[] { HttpMethod.Get }));
        Assert.That(fixture.Host.Connector.Bodies, Is.Empty);
        Assert.That(fixture.Host.Connector.Destinations.Single().AbsoluteUri,
            Is.EqualTo("https://smba.trafficmanager.net/emea/v3/conversations/private%3Aconversation%2Fwith%2Bspace%20%3F/members"));
        string json = await response.Content.ReadAsStringAsync();
        MessageReplyDiagnosis result = Json.Read<MessageReplyDiagnosis>(json);
        Assert.That(result.Completed, Is.False);
        Assert.That(result.HttpStatus, Is.EqualTo(401));
        Assert.That(result.Reason, Is.EqualTo("authorization_denied_generic"));
        Assert.That(new[] { result.HeaderSingleBearer, result.JwtReadable, result.AudienceMatchesConnectorResource,
            result.AuthorizedPartyMatchesConfiguredBot, result.IssuerMatchesKnownBotFrameworkIssuer, result.ValidityWindowMatchesNow,
            result.OutboundRouteMatchesRetainedOrigin, result.RequestContextBotMatchesConfiguredBot }, Is.All.True);
        Assert.That(result.AuthenticationChallenge, Is.EqualTo("bearer"));
        Assert.That(result.AuthenticationChallengeError, Is.EqualTo("absent"));
        Assert.That(result.AuthenticationChallengeAuthority, Is.EqualTo("absent"));
        Assert.That(result.AuthenticationChallengeResourceMatchesConnector, Is.Null);
        Assert.That(Json.WriteString(await database.Entities.Load<TeamsMessageOrigin>().ExecuteEntityAsync()), Is.EqualTo(Json.WriteString(before)));
        Assert.That(new[] { json }.Concat(capture.Messages).Any(value => value.Contains(token) || value.Contains(caller) ||
            value.Contains(before.ServiceUrl) || value.Contains(before.ConversationId) || value.Contains(TeamsWebhookFixture.ClientId)), Is.False);
    }

    /// <summary>Checks decoded token comparisons expose mismatches while unreadable or unavailable claims stay inconclusive.</summary>
    /// <param name="scenario">The synthetic native token profile.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase("matching"), TestCase("v2-issuer"), TestCase("wrong-audience"), TestCase("wrong-party"), TestCase("conflicting-parties")]
    [TestCase("wrong-issuer"), TestCase("expired"), TestCase("not-yet-valid"), TestCase("missing-claims")]
    [TestCase("opaque"), TestCase("malformed"), TestCase("invalid-lifetime"), TestCase("double-bearer"), TestCase("empty"), Parallelizable]
    public async Task Diagnose_NativeToken_ReportsOnlyAvailableProfileFacts(string scenario) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        string token = scenario == "empty" ? "" : Token(scenario);
        Mock<IAuthorizationHeaderProvider> provider = Provider((scenario == "double-bearer" ? "Bearer Bearer " : "Bearer ") + token);
        TeamsLogCapture capture = new();
        using TeamsRoutingFixture fixture = Fixture(database, provider, capture);
        long id = await Receive(fixture);
        fixture.Host.Connector.ResponseBody = "[]";

        using HttpResponseMessage response = await fixture.Diagnose(id);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(fixture.Host.Connector.Methods.ToArray(), Is.EqualTo(new[] { HttpMethod.Get }));
        string json = await response.Content.ReadAsStringAsync();
        MessageReplyDiagnosis result = Json.Read<MessageReplyDiagnosis>(json);
        Assert.That(result.Completed, Is.True);
        Assert.That(result.HeaderSingleBearer, Is.EqualTo(scenario is not ("double-bearer" or "empty")));
        Assert.That(result.JwtReadable, Is.EqualTo(scenario == "empty" ? null : scenario is not ("opaque" or "malformed" or "double-bearer" or "invalid-lifetime")));
        if (scenario is "opaque" or "malformed" or "double-bearer" or "empty" or "missing-claims" or "invalid-lifetime")
            Assert.That(new[] { result.AudienceMatchesConnectorResource, result.AuthorizedPartyMatchesConfiguredBot,
                result.IssuerMatchesKnownBotFrameworkIssuer, result.ValidityWindowMatchesNow }, Is.All.Null);
        else {
            Assert.That(result.AudienceMatchesConnectorResource, Is.EqualTo(scenario != "wrong-audience"));
            Assert.That(result.AuthorizedPartyMatchesConfiguredBot, Is.EqualTo(scenario is not ("wrong-party" or "conflicting-parties")));
            Assert.That(result.IssuerMatchesKnownBotFrameworkIssuer, Is.EqualTo(scenario != "wrong-issuer"));
            Assert.That(result.ValidityWindowMatchesNow, Is.EqualTo(scenario is not ("expired" or "not-yet-valid")));
        }
        Assert.That(result.OutboundRouteMatchesRetainedOrigin, Is.True);
        Assert.That(result.RequestContextBotMatchesConfiguredBot, Is.True);
        Assert.That(result.AuthenticationChallenge, Is.EqualTo("absent"));
        Assert.That(result.AuthenticationChallengeAuthority, Is.EqualTo("absent"));
        Assert.That(new[] { json }.Concat(capture.Messages).Any(value =>
            value.Contains("private-token-canary") || token.Length > 0 && value.Contains(token)), Is.False);
    }

    /// <summary>Checks the host profile observes its own issuer without adopting activity or shared-tenant issuers.</summary>
    /// <param name="scenario">The synthetic token issuer profile.</param>
    /// <param name="expected">The expected available issuer comparison.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase("v1", true), TestCase("v2", true), TestCase("legacy", false), TestCase("activity", false)]
    [TestCase("opaque", null), TestCase("missing-claims", null), Parallelizable]
    [Description("#33857: observe the configured hosting issuer independently of the activity tenant.")]
    public async Task Diagnose_HostTenant_ObservesOnlySelectedIssuer(string scenario, bool? expected) {
        const string HostingTenant = "8165ad9e-f318-4192-b1bf-3ab83959819f";
        string issuer = scenario switch {
            "v1" => Rest.Path("https://sts.windows.net", HostingTenant) + "/",
            "v2" => Rest.Path("https://login.microsoftonline.com", HostingTenant, "v2.0"),
            "activity" => Rest.Path("https://sts.windows.net", TeamsWebhookFixture.TenantId) + "/",
            _ => "https://sts.windows.net/d6d49420-f39b-4df7-a1dc-d59a935871db/"
        };
        string token = Token(scenario, issuer);
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        TeamsLogCapture capture = new();
        Mock<IAuthorizationHeaderProvider> provider = Provider("Bearer " + token);
        using TeamsRoutingFixture fixture = Fixture(database, provider, capture, new() { ["AzureAd:TenantId"] = HostingTenant });
        long id = await Receive(fixture);
        TeamsMessageOrigin before = await database.Entities.Load<TeamsMessageOrigin>().ExecuteEntityAsync();
        fixture.Host.Connector.ResponseBody = "[]";

        using HttpResponseMessage response = await fixture.Diagnose(id);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(fixture.Host.Connector.Methods.ToArray(), Is.EqualTo(new[] { HttpMethod.Get }));
        string json = await response.Content.ReadAsStringAsync();
        MessageReplyDiagnosis result = Json.Read<MessageReplyDiagnosis>(json);
        Assert.That(result.Completed, Is.True);
        Assert.That(result.HeaderSingleBearer, Is.True);
        Assert.That(result.OutboundRouteMatchesRetainedOrigin, Is.True);
        Assert.That(result.RequestContextBotMatchesConfiguredBot, Is.True);
        Assert.That(result.IssuerMatchesKnownBotFrameworkIssuer, Is.EqualTo(expected));
        Assert.That(Json.WriteString(await database.Entities.Load<TeamsMessageOrigin>().ExecuteEntityAsync()), Is.EqualTo(Json.WriteString(before)));
        Assert.That(new[] { json }.Concat(capture.Messages).Any(value => value.Contains(token) ||
            value.Contains(HostingTenant) || value.Contains(issuer) || value.Contains("private-token-canary")), Is.False);
    }

    /// <summary>Checks response challenges expose only fixed classes without following supplied authorities.</summary>
    /// <param name="scenario">The response challenge syntax and profile.</param>
    /// <param name="challenge">The expected safe challenge class.</param>
    /// <param name="error">The expected allowlisted error class.</param>
    /// <param name="authority">The expected explicit authority class.</param>
    /// <param name="resource">The expected explicit resource comparison.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase("bare", "bearer", "absent", "absent", null)]
    [TestCase("matching", "bearer", "invalid_token", "public_botframework", true)]
    [TestCase("other-tenant", "bearer", "invalid_token", "other_tenant", true)]
    [TestCase("wrong-resource", "bearer", "insufficient_scope", "absent", false)]
    [TestCase("unknown", "bearer", "unrecognized", "unrecognized", null)]
    [TestCase("malformed", "unrecognized", "absent", "absent", null)]
    [TestCase("duplicate", "bearer", "unrecognized", "unrecognized", null)]
    [TestCase("ambiguous", "bearer", "invalid_request", "ambiguous", null)]
    [TestCase("quoted-comma", "bearer", "invalid_token", "absent", null)]
    [TestCase("other", "other", "absent", "absent", null)]
    [TestCase("multiple", "multiple_bearer", "absent", "absent", null)]
    [TestCase("oversized", "bearer", "unrecognized", "unrecognized", null)]
    [TestCase("raw-invalid", "unrecognized", "absent", "absent", null), Parallelizable]
    public async Task Diagnose_ResponseChallenge_ClassifiesWithoutPrivateValues(string scenario, string challenge, string error, string authority, bool? resource) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        TeamsLogCapture capture = new();
        using TeamsRoutingFixture fixture = Fixture(database, Provider("Bearer " + Token("matching")), capture);
        long id = await Receive(fixture);
        TeamsMessageOrigin before = await database.Entities.Load<TeamsMessageOrigin>().ExecuteEntityAsync();
        const string Canary = "private-challenge-canary";
        const string OtherTenant = "18a7a04d-95fb-488c-a94b-57c246064ac0";
        fixture.Host.Connector.Status = HttpStatusCode.Unauthorized;
        fixture.Host.Connector.ResponseBody = "{\"message\":\"Authorization has been denied for this request.\"}";
        fixture.Host.Connector.AuthenticationChallenges = scenario switch {
            "matching" => ["Bearer error=\"invalid_token\", authorization_uri=\"https://login.microsoftonline.com/botframework.com/oauth2/v2.0/authorize\", resource=\"https://api.botframework.com\""],
            "other-tenant" => ["Bearer error=\"invalid_token\", issuer=\"https://sts.windows.net/" + OtherTenant + "/\", resource_id=\"https://api.botframework.com\""],
            "wrong-resource" => ["Bearer error=\"insufficient_scope\", resource=\"https://private-resource.invalid/" + Canary + "\""],
            "unknown" => ["Bearer error=\"" + Canary + "\", authorization_uri=\"https://private-authority.invalid/" + Canary + "\""],
            "malformed" => ["Bearer error=\"" + Canary],
            "duplicate" => ["Bearer error=\"invalid_token\", error=\"" + Canary + "\""],
            "ambiguous" => ["Bearer error=\"invalid_request\", authorization_uri=\"https://login.microsoftonline.com/botframework.com\", issuer=\"https://sts.windows.net/" + OtherTenant + "/\""],
            "quoted-comma" => ["Bearer realm=\"" + Canary + ",\\\"quoted\\\"\", error=\"invalid_token\""],
            "other" => ["Basic realm=\"" + Canary + "\""],
            "multiple" => ["Bearer", "Bearer realm=\"" + Canary + "\""],
            "oversized" => ["Bearer realm=\"" + Canary + new string('x', 8192) + "\""],
            "raw-invalid" => ["\\"], _ => ["Bearer"]
        };

        using HttpResponseMessage response = await fixture.Diagnose(id);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(fixture.Host.Connector.Methods.ToArray(), Is.EqualTo(new[] { HttpMethod.Get }));
        Assert.That(fixture.Host.Connector.Destinations.Single().Host, Is.EqualTo("smba.trafficmanager.net"));
        string json = await response.Content.ReadAsStringAsync();
        MessageReplyDiagnosis result = Json.Read<MessageReplyDiagnosis>(json);
        Assert.That(result.HttpStatus, Is.EqualTo(401));
        Assert.That(result.AuthenticationChallenge, Is.EqualTo(challenge));
        Assert.That(result.AuthenticationChallengeError, Is.EqualTo(error));
        Assert.That(result.AuthenticationChallengeAuthority, Is.EqualTo(authority));
        Assert.That(result.AuthenticationChallengeResourceMatchesConnector, Is.EqualTo(resource));
        Assert.That(Json.WriteString(await database.Entities.Load<TeamsMessageOrigin>().ExecuteEntityAsync()), Is.EqualTo(Json.WriteString(before)));
        Assert.That(new[] { json }.Concat(capture.Messages).Any(value => value.Contains(Canary) || value.Contains(OtherTenant) ||
            value.Contains("private-authority.invalid") || value.Contains("private-resource.invalid")), Is.False);
    }

    static TeamsRoutingFixture Fixture(TeamsRoutingDatabase database, Mock<IAuthorizationHeaderProvider> provider, TeamsLogCapture capture,
        Dictionary<string, string?>? overrides = null) =>
        new(database, overrides: overrides, preserveOutboundAuthentication: true, configureServices: services => {
            services.RemoveAll<IAuthorizationHeaderProvider>();
            services.AddSingleton(provider.Object);
            services.AddLogging(logging => logging.AddProvider(capture));
        });

    static Mock<IAuthorizationHeaderProvider> Provider(string header) {
        Mock<IAuthorizationHeaderProvider> provider = new(MockBehavior.Strict);
        provider.Setup(auth => auth.CreateAuthorizationHeaderForAppAsync(It.IsAny<string>(),
            It.IsAny<AuthorizationHeaderProviderOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(header);
        return provider;
    }

    static async Task<long> Receive(TeamsRoutingFixture fixture, string? conversation = null) {
        Dictionary<string, object?> activity = TeamsWebhookFixture.Activity();
        if (conversation != null)
            activity["conversation"] = new Dictionary<string, object> { ["id"] = conversation, ["tenantId"] = TeamsWebhookFixture.TenantId };
        using HttpResponseMessage response = await fixture.Host.Post(fixture.Host.Token(), activity);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(fixture.Host.Connector.Methods, Is.Empty);
        return (await fixture.Database.Entities.Load<Message>().ExecuteEntityAsync()).Id;
    }

    static string Token(string scenario, string? issuer = null) {
        if (scenario == "opaque")
            return "private-token-canary";
        if (scenario == "malformed")
            return "eyJhbGciOiJub25lIn0.ew.";
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Dictionary<string, object> claims = scenario == "missing-claims" ? new() : new() {
            ["aud"] = scenario == "wrong-audience" ? "https://private-resource.invalid" : "https://api.botframework.com",
            ["iss"] = issuer ?? scenario switch {
                "wrong-issuer" => "https://sts.windows.net/18a7a04d-95fb-488c-a94b-57c246064ac0/",
                "v2-issuer" => "https://login.microsoftonline.com/d6d49420-f39b-4df7-a1dc-d59a935871db/v2.0",
                _ => "https://sts.windows.net/d6d49420-f39b-4df7-a1dc-d59a935871db/"
            },
            ["appid"] = scenario == "wrong-party" ? "18a7a04d-95fb-488c-a94b-57c246064ac0" : TeamsWebhookFixture.ClientId,
            ["nbf"] = scenario == "not-yet-valid" ? now + 600 : now - 600,
            ["exp"] = scenario == "expired" ? now - 60 : now + 1200,
            ["private"] = "private-token-canary"
        };
        if (scenario == "conflicting-parties")
            claims["azp"] = "18a7a04d-95fb-488c-a94b-57c246064ac0";
        if (scenario == "invalid-lifetime")
            claims["exp"] = "private-token-canary";
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.WriteString(claims)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "eyJhbGciOiJub25lIn0." + encoded + ".";
    }
}
