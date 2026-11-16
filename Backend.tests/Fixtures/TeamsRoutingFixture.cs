using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Backend.Models.Messages;
using Backend.Services.Messages;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Pooshit.Json;
using Pooshit.Http.Paths;
using Pooshit.Ocelot.Entities;

namespace Backend.tests.Fixtures;

/// <summary>A real inbox and signed HTTP Teams transport with a captured native Connector.</summary>
public sealed class TeamsRoutingFixture : IDisposable {
    readonly RSA rsa = RSA.Create(2048);
    readonly RsaSecurityKey key;

    /// <summary>Creates the configured test host over the isolated database.</summary>
    /// <param name="database">The isolated persistence fixture.</param>
    /// <param name="overrides">The optional integration configuration overrides.</param>
    public TeamsRoutingFixture(TeamsRoutingDatabase database, Dictionary<string, string?>? overrides = null) {
        Database = database;
        key = new RsaSecurityKey(rsa) { KeyId = "routing-keycloak" };
        Dictionary<string, string?> config = new() {
            ["Teams:Target:UserId"] = database.RecipientId.ToString(),
            ["Teams:Target:Tag"] = "divoid",
            ["Teams:BridgeUserId"] = database.BridgeId.ToString()
        };
        if (overrides != null)
            foreach (KeyValuePair<string, string?> item in overrides)
                config[item.Key] = item.Value;
        Host = new TeamsWebhookFixture(overrides: config, configureServices: services => {
            services.RemoveAll<IEntityManager>();
            services.AddSingleton(database.Entities);
            services.RemoveAll<IMessageService>();
            services.AddTransient<IMessageService>(provider => provider.GetRequiredService<MessageService>());
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options => {
                options.Configuration = new OpenIdConnectConfiguration { Issuer = "https://keycloak.invalid/realm" };
                options.Configuration.SigningKeys.Add(key);
            });
        });
    }

    /// <summary>The isolated durable inbox.</summary>
    public TeamsRoutingDatabase Database { get; }

    /// <summary>The actual authenticated HTTP host and captured Connector.</summary>
    public TeamsWebhookFixture Host { get; }

    /// <summary>Creates a real DiVoid recipient credential.</summary>
    /// <param name="userId">The existing actor identifier.</param>
    /// <returns>The locally signed Keycloak-profile token.</returns>
    public string UserToken(long userId) => Host.Token(issuer: "https://keycloak.invalid/realm", audience: "divoid-tests",
        key: key, claims: [new Claim("userId", userId.ToString())]);

    /// <summary>Sends one explicit recipient reply through the public API.</summary>
    /// <param name="messageId">The original inbox identifier.</param>
    /// <param name="body">The explicit response text.</param>
    /// <param name="userId">The optional calling actor.</param>
    /// <returns>The public HTTP response.</returns>
    public Task<HttpResponseMessage> Reply(long messageId, string body = "reply text", long? userId = null) =>
        Host.Send(HttpMethod.Post, Rest.Path("api/messages", messageId, "reply"), UserToken(userId ?? Database.RecipientId),
            new StringContent(Json.WriteString(new MessageReplyBody { Body = body }), Encoding.UTF8, "application/json"));

    /// <summary>Reads the public recipient receipt.</summary>
    /// <param name="messageId">The original inbox identifier.</param>
    /// <param name="userId">The optional calling actor.</param>
    /// <returns>The public receipt response.</returns>
    public Task<HttpResponseMessage> Status(long messageId, long? userId = null) =>
        Host.Send(HttpMethod.Get, Rest.Path("api/messages", messageId, "reply"), UserToken(userId ?? Database.RecipientId));

    /// <inheritdoc />
    public void Dispose() {
        Host.Dispose();
        rsa.Dispose();
    }
}
