using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Backend.Services.Messages;
using Backend.Models.Messages;
using Backend.Services.Teams;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Pooshit.Http;
using Pooshit.Http.Paths;
using Pooshit.Json;
using Pooshit.Ocelot.Entities;

namespace Backend.tests.Fixtures;

/// <summary>Runs an isolated Teams webhook with local JWT authority and SDK Connector transport.</summary>
public sealed class TeamsWebhookFixture : IDisposable {
    /// <summary>The synthetic bot application identifier.</summary>
    public const string ClientId = "4d9530dd-a8f0-4c36-af2f-aa8d3fdaec7c";

    /// <summary>The sole permitted synthetic tenant.</summary>
    public const string TenantId = "51a42af7-86b3-47ac-b238-e73478da4e71";

    /// <summary>The signed synthetic Connector service URL.</summary>
    public const string ServiceUrl = "https://smba.trafficmanager.net/emea/";
    readonly RSA rsa = RSA.Create(2048);
    readonly WebApplicationFactory<Program> factory;
    readonly IHttpService http;
    readonly TeamsRoutingDatabase? routingDatabase;

    /// <summary>Creates an isolated webhook host.</summary>
    /// <param name="authEnabled">Whether DiVoid authentication is enabled.</param>
    /// <param name="overrides">The optional configuration overrides.</param>
    /// <param name="configureServices">The optional service registration overrides.</param>
    /// <param name="graphBinding">Whether to install a real isolated protected graph binding.</param>
    /// <param name="preserveOutboundAuthentication">Whether the native SDK credential handler is retained.</param>
    public TeamsWebhookFixture(bool authEnabled = true, Dictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configureServices = null, bool preserveOutboundAuthentication = false, bool graphBinding = false) {
        if (graphBinding)
            routingDatabase = TeamsRoutingDatabase.CreateAsync(false).GetAwaiter().GetResult();
        Key = new RsaSecurityKey(rsa) { KeyId = "teams-first" };
        Metadata = new TeamsMetadataHandler(Key);
        Dictionary<string, string?> config = new() {
            ["Auth:Enabled"] = authEnabled.ToString(),
            ["Keycloak:Audience"] = "divoid-tests",
            ["Keycloak:Authority"] = "https://keycloak.invalid/realm",
            ["Teams:Enabled"] = "true",
            ["AzureAd:ClientId"] = ClientId,
            ["AzureAd:ClientCredentials:0:SourceType"] = "ClientSecret",
            ["AzureAd:ClientCredentials:0:ClientSecret"] = "synthetic-fixture-secret",
            ["DIVOID_KEY_PEPPER"] = "teams-test-pepper-0000000000000000000000000"
        };
        if (overrides != null)
            foreach (KeyValuePair<string, string?> value in overrides)
                config[value.Key] = value.Value;
        factory = TestSetup.CreateTestFactory().WithWebHostBuilder(builder => {
            builder.ConfigureAppConfiguration((_, builderConfiguration) => builderConfiguration.AddInMemoryCollection(config));
            builder.ConfigureTestServices(services => {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IEntityManager>();
                services.AddSingleton(Database.Object);
                services.RemoveAll<IMessageService>();
                services.AddSingleton(Messages.Object);
                services.Configure<JwtBearerOptions>("TeamsBot", options => options.BackchannelHttpHandler = Metadata);
                services.ConfigureAll<HttpClientFactoryOptions>(options => options.HttpMessageHandlerBuilderActions.Add(builder => {
                    if (!preserveOutboundAuthentication)
                        builder.AdditionalHandlers.Clear();
                    builder.PrimaryHandler = Connector;
                }));
                if (routingDatabase != null) {
                    services.RemoveAll<IEntityManager>();
                    services.AddSingleton(routingDatabase.Entities);
                    services.RemoveAll<IMessageService>();
                    services.AddTransient<IMessageService, MessageService>();
                }
                configureServices?.Invoke(services);
            });
        });
        http = TestSetup.HttpServiceFor(factory);
    }

    /// <summary>The synthetic Connector signing key.</summary>
    public RsaSecurityKey Key { get; }

    /// <summary>The local authoritative metadata transport.</summary>
    public TeamsMetadataHandler Metadata { get; }

    /// <summary>The captured outbound SDK transport.</summary>
    public TeamsConnectorHandler Connector { get; } = new();

    /// <summary>The strict persistence boundary spy.</summary>
    public Mock<IEntityManager> Database { get; } = new(MockBehavior.Strict);

    /// <summary>The strict DiVoid routing boundary spy.</summary>
    public Mock<IMessageService> Messages { get; } = new(MockBehavior.Strict);

    /// <summary>The application service provider.</summary>
    public IServiceProvider Services => factory.Services;

    /// <summary>Explicitly replies to the message queued by this fixture's real graph transport.</summary>
    /// <returns>The recipient-authorized local transport receipt.</returns>
    public async Task<MessageReplyReceipt> ReplyToQueuedMessage() {
        Message message = await routingDatabase!.Entities.Load<Message>().ExecuteEntityAsync();
        return await Services.GetRequiredService<TeamsMessageService>().ReplyAsync(routingDatabase.RecipientId,
            message.Id, "explicit fixture reply", CancellationToken.None);
    }

    /// <summary>Mints a synthetic Connector JWT.</summary>
    /// <param name="issuer">The token issuer.</param>
    /// <param name="audience">The token audience.</param>
    /// <param name="notBefore">The optional validity start.</param>
    /// <param name="expires">The optional expiration.</param>
    /// <param name="key">The optional signing key.</param>
    /// <param name="claims">The optional token claims.</param>
    /// <param name="algorithm">The signing algorithm.</param>
    /// <returns>The signed token.</returns>
    public string Token(string issuer = "https://api.botframework.com", string audience = ClientId,
        DateTime? notBefore = null, DateTime? expires = null, RsaSecurityKey? key = null,
        Claim[]? claims = null, string algorithm = SecurityAlgorithms.RsaSha256) {
        JwtSecurityToken token = new(issuer, audience,
            claims ?? [new Claim("serviceUrl", ServiceUrl)],
            notBefore ?? DateTime.UtcNow.AddMinutes(-1), expires ?? DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(key ?? Key, algorithm));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Creates a fresh native activity wire payload.</summary>
    /// <returns>A fresh setup activity payload.</returns>
    public static Dictionary<string, object?> Activity() => new() {
        ["type"] = "message", ["id"] = "activity-1", ["channelId"] = "msteams",
        ["serviceUrl"] = ServiceUrl, ["text"] = "sensitive text must never be echoed",
        ["channelData"] = new Dictionary<string, object> {
            ["tenant"] = new Dictionary<string, object> { ["id"] = TenantId }
        },
        ["conversation"] = new Dictionary<string, object> { ["id"] = "conversation-1", ["tenantId"] = TenantId },
        ["from"] = new Dictionary<string, object> { ["id"] = "sender-1" },
        ["recipient"] = new Dictionary<string, object> { ["id"] = "28:" + ClientId }
    };

    /// <summary>Posts a synthetic activity.</summary>
    /// <param name="token">The optional bearer credential.</param>
    /// <param name="activity">The optional activity payload.</param>
    /// <returns>The webhook response.</returns>
    public Task<HttpResponseMessage> Post(string? token, Dictionary<string, object?>? activity = null) =>
        PostBody(token, new StringContent(Json.WriteString(activity ?? Activity()), Encoding.UTF8, "application/json"));

    /// <summary>Posts raw content through the application HTTP pipeline.</summary>
    /// <param name="token">The optional bearer credential.</param>
    /// <param name="content">The request content.</param>
    /// <param name="path">The application route.</param>
    /// <returns>The application response.</returns>
    public Task<HttpResponseMessage> PostBody(string? token, HttpContent content, string path = "api/teams/messages") =>
        Send(HttpMethod.Post, path, token, content);

    /// <summary>Sends an authenticated request through the test server.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">The application route.</param>
    /// <param name="token">The optional bearer credential.</param>
    /// <param name="content">The optional request content.</param>
    /// <returns>The application response.</returns>
    public async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? token = null, HttpContent? content = null) {
        using HttpRequestMessage request = await http.CreateRequest(Rest.Path(TestSetup.BaseUrl, path), method, null);
        request.Content = content;
        if (token != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.Send<HttpResponseMessage>(request, null);
    }

    /// <inheritdoc />
    public void Dispose() {
        factory.Dispose();
        routingDatabase?.Dispose();
        rsa.Dispose();
    }
}
