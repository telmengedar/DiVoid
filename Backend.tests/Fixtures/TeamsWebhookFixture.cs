using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Backend.Services.Messages;
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

    /// <summary>Creates an isolated webhook host.</summary>
    public TeamsWebhookFixture(bool authEnabled = true, Dictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configureServices = null) {
        Key = new RsaSecurityKey(rsa) { KeyId = "teams-first" };
        Metadata = new TeamsMetadataHandler(Key);
        Dictionary<string, string?> config = new() {
            ["Auth:Enabled"] = authEnabled.ToString(),
            ["Keycloak:Audience"] = "divoid-tests",
            ["Keycloak:Authority"] = "https://keycloak.invalid/realm",
            ["Teams:Enabled"] = "true",
            ["AzureAd:ClientId"] = ClientId,
            ["AzureAd:TenantId"] = TenantId,
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
                    builder.AdditionalHandlers.Clear();
                    builder.PrimaryHandler = Connector;
                }));
                configureServices?.Invoke(services);
            });
        });
        http = TestSetup.HttpServiceFor(factory);
    }

    /// <summary>Mints a synthetic Connector JWT.</summary>
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
    public Task<HttpResponseMessage> Post(string? token, Dictionary<string, object?>? activity = null) =>
        PostBody(token, new StringContent(Json.WriteString(activity ?? Activity()), Encoding.UTF8, "application/json"));

    /// <summary>Posts raw content through the application HTTP pipeline.</summary>
    public async Task<HttpResponseMessage> PostBody(string? token, HttpContent content, string path = "api/teams/messages") {
        using HttpRequestMessage request = await http.CreateRequest(Rest.Path(TestSetup.BaseUrl, path), HttpMethod.Post, null);
        request.Content = content;
        if (token != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.Send<HttpResponseMessage>(request, null);
    }

    /// <summary>Releases the test host and signing key.</summary>
    public void Dispose() {
        factory.Dispose();
        rsa.Dispose();
    }
}
