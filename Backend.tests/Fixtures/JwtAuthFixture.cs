#nullable enable
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Pooshit.Ocelot.Entities;

namespace Backend.tests.Fixtures;

/// <summary>
/// Integration test fixture that boots the full ASP.NET Core pipeline with
/// <c>Auth:Enabled=true</c> and wires a locally-generated RSA key as the
/// Keycloak signing key. A custom backchannel handler intercepts OIDC metadata
/// and JWKS discovery so no live Keycloak instance is required in CI.
/// </summary>
public sealed class JwtAuthFixture : IDisposable
{
    public const string TestAuthority = "https://test-keycloak.local/realms/master";
    public const string TestAudience  = "divoid-test-client";
    public const string TestPepper    = "jwt-auth-fixture-test-pepper-32-bytes-minimum-0000";

    readonly RSA rsa;
    readonly RsaSecurityKey signingKey;
    readonly WebApplicationFactory<Program> factory;
    readonly HttpMessageHandler serverHandler;

    public JwtAuthFixture()
    {
        rsa        = RSA.Create(2048);
        signingKey = new RsaSecurityKey(rsa) { KeyId = "test-key-1" };

        string jwks          = BuildJwks();
        string discoveryJson = BuildDiscoveryDocument();

        string dbPath = $"/tmp/divoid_jwt_auth_test_{Guid.NewGuid():N}.db3";

        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => {
            builder.ConfigureAppConfiguration((_, config) => {
                config.AddInMemoryCollection(new Dictionary<string, string?> {
                    ["Auth:Enabled"]                  = "true",
                    ["DIVOID_KEY_PEPPER"]             = TestPepper,
                    ["Database:Type"]                 = "Sqlite",
                    ["Database:Source"]               = dbPath,
                    ["Keycloak:Authority"]            = TestAuthority,
                    ["Keycloak:Audience"]             = TestAudience,
                    ["Keycloak:RequireHttpsMetadata"] = "false",
                    ["Keycloak:UserIdClaimName"]      = "userId"
                });
            });
            builder.ConfigureServices(services => {
                // Replace the JwtBearer backchannel handler and override signing key directly
                // so the middleware never makes a real network call to Keycloak.
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options => {
                    options.BackchannelHttpHandler = new FakeJwksHandlerPublic(TestAuthority, discoveryJson, jwks);
                    options.TokenValidationParameters.IssuerSigningKey  = signingKey;
                    options.TokenValidationParameters.IssuerSigningKeys = [signingKey];
                });
            });
        });

        // CreateHandler returns the in-process server's HttpMessageHandler —
        // wrap it in an HttpClient to keep everything in-process.
        serverHandler = factory.Server.CreateHandler();
        EntityManager = factory.Services.GetRequiredService<IEntityManager>();
    }

    /// <summary>entity manager for direct DB setup in tests</summary>
    public IEntityManager EntityManager { get; }

    /// <summary>
    /// creates an <see cref="HttpClient"/> that routes requests through
    /// the in-process test server. Each call returns a new client so tests
    /// can set different <c>Authorization</c> headers independently.
    /// </summary>
    public HttpClient CreateClient() => new(serverHandler) { BaseAddress = new Uri(TestSetup.BaseUrl) };

    /// <summary>
    /// mints a signed JWT with the test RSA key, valid for 5 minutes by default
    /// </summary>
    public string MintToken(
        string?  subject   = "test-sub",
        long?    userId    = null,
        string?  audience  = TestAudience,
        string?  issuer    = TestAuthority,
        DateTime? notBefore = null,
        DateTime? expires  = null,
        bool     useWrongKey = false,
        string   userIdClaimName = "userId")
    {
        DateTime now = DateTime.UtcNow;

        List<Claim> claims = new();
        if (subject != null)    claims.Add(new Claim(JwtRegisteredClaimNames.Sub, subject));
        if (userId.HasValue)    claims.Add(new Claim(userIdClaimName, userId.Value.ToString()));

        SigningCredentials creds;
        RSA? wrongRsa = null;
        if (useWrongKey) {
            wrongRsa = RSA.Create(2048);
            // Note: do NOT dispose wrongRsa until after WriteToken completes —
            // the JwtSecurityTokenHandler uses the key lazily during serialization.
            creds = new SigningCredentials(
                new RsaSecurityKey(wrongRsa),
                SecurityAlgorithms.RsaSha256);
        } else {
            creds = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256);
        }

        JwtSecurityToken token = new(
            issuer:             issuer,
            audience:           audience,
            claims:             claims,
            notBefore:          notBefore ?? now.AddSeconds(-5),
            expires:            expires   ?? now.AddMinutes(5),
            signingCredentials: creds);

        string jwt = new JwtSecurityTokenHandler().WriteToken(token);
        wrongRsa?.Dispose();
        return jwt;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        factory.Dispose();
        serverHandler.Dispose();
        rsa.Dispose();
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    string BuildJwks()
    {
        RSAParameters p = rsa.ExportParameters(false);
        return JsonSerializer.Serialize(new {
            keys = new[] {
                new {
                    kty = "RSA",
                    use = "sig",
                    kid = signingKey.KeyId,
                    alg = "RS256",
                    n   = Base64UrlEncoder.Encode(p.Modulus!),
                    e   = Base64UrlEncoder.Encode(p.Exponent!)
                }
            }
        });
    }

    static string BuildDiscoveryDocument() =>
        JsonSerializer.Serialize(new {
            issuer                                  = TestAuthority,
            jwks_uri                                = $"{TestAuthority}/protocol/openid-connect/certs",
            authorization_endpoint                  = $"{TestAuthority}/protocol/openid-connect/auth",
            token_endpoint                          = $"{TestAuthority}/protocol/openid-connect/token",
            id_token_signing_alg_values_supported   = new[] { "RS256" }
        });

    // -----------------------------------------------------------------------
    // Inner handler that serves canned OIDC metadata + JWKS responses
    // -----------------------------------------------------------------------

    /// <summary>exposes the fake JWKS handler for use in other test fixtures</summary>
    public sealed class FakeJwksHandlerPublic : HttpMessageHandler
    {
        readonly string authority;
        readonly string discoveryJson;
        readonly string jwksJson;

        public FakeJwksHandlerPublic(string authority, string discoveryJson, string jwksJson)
        {
            this.authority     = authority;
            this.discoveryJson = discoveryJson;
            this.jwksJson      = jwksJson;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage  request,
            CancellationToken   cancellationToken)
        {
            string url = request.RequestUri?.ToString() ?? "";

            if (url.Contains(".well-known/openid-configuration"))
                return Task.FromResult(JsonResponse(discoveryJson));

            if (url.Contains("/protocol/openid-connect/certs") ||
                (url.Contains("/certs") && url.StartsWith(authority, StringComparison.Ordinal)))
                return Task.FromResult(JsonResponse(jwksJson));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) {
                Content = new StringContent(
                    $"FakeJwksHandlerPublic: unexpected URL {url}", Encoding.UTF8, "text/plain")
            });
        }

        static HttpResponseMessage JsonResponse(string json) =>
            new(HttpStatusCode.OK) {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
    }
}
