#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Backend.Models.Auth;
using Backend.Models.Users;
using Backend.Services.Auth;
using Backend.tests.Fixtures;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Pooshit.Json;
using Pooshit.Ocelot.Entities;

namespace Backend.tests.Tests;

/// <summary>
/// Integration tests for Keycloak (OIDC) JWT authentication.
///
/// Uses a locally-generated RSA key and an in-process JWKS handler so
/// no live Keycloak instance is required in CI.
///
/// Covers the seven cases specified in M4 of the design doc plus a
/// cross-scheme regression (case 8):
///   1. Happy path — valid JWT + enabled user with read perm → 200 on GET /api/nodes
///   2. Valid JWT, missing UserId claim → 403
///   3. Valid JWT, UserId present, no divoid_user row → 403
///   4. Valid JWT, UserId present, divoid_user.Enabled=false → 403
///   5. JWT signed by an unknown key → 401
///   6. Expired JWT → 401
///   7. JWT with wrong audience → 401
///   8. ApiKey scheme still works alongside JwtBearer (cross-scheme regression)
/// </summary>
[TestFixture]
public class JwtAuthTests
{
    JwtAuthFixture fixture = null!;
    IEntityManager db = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        fixture = new JwtAuthFixture();
        db      = fixture.EntityManager;
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        fixture.Dispose();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    async Task<long> CreateEnabledUserAsync(string? permissionsJson = null)
    {
        return await db.Insert<User>()
                       .Columns(u => u.Name, u => u.Email, u => u.Enabled, u => u.Permissions, u => u.CreatedAt)
                       .Values("jwt-test-user", "test@example.com", true, permissionsJson, DateTime.UtcNow)
                       .ReturnID()
                       .ExecuteAsync();
    }

    async Task<long> CreateDisabledUserAsync(string? permissionsJson = null)
    {
        return await db.Insert<User>()
                       .Columns(u => u.Name, u => u.Email, u => u.Enabled, u => u.Permissions, u => u.CreatedAt)
                       .Values("jwt-disabled-user", "disabled@example.com", false, permissionsJson, DateTime.UtcNow)
                       .ReturnID()
                       .ExecuteAsync();
    }

    Task<HttpResponseMessage> GetNodesAsync(string token)
    {
        HttpClient client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.GetAsync("/api/nodes");
    }

    // -----------------------------------------------------------------------
    // Case 1 — Happy path: valid JWT + enabled user with read permission → 200
    // -----------------------------------------------------------------------

    [Test]
    public async Task ValidJwt_EnabledUserWithReadPermission_Returns200()
    {
        long userId = await CreateEnabledUserAsync(
            permissionsJson: Json.WriteString(new[] { "read" }));

        string token = fixture.MintToken(userId: userId);
        HttpResponseMessage response = await GetNodesAsync(token);

        Assert.That((int)response.StatusCode, Is.EqualTo(200),
            "valid JWT with enabled user + read permission must return 200 on GET /api/nodes");
    }

    // -----------------------------------------------------------------------
    // Case 2 — Valid JWT, no UserId claim → 403
    // -----------------------------------------------------------------------

    [Test]
    public async Task ValidJwt_NoUserIdClaim_Returns403()
    {
        string token = fixture.MintToken(userId: null); // omit UserId claim
        HttpResponseMessage response = await GetNodesAsync(token);

        Assert.That((int)response.StatusCode, Is.EqualTo(403),
            "valid JWT without UserId claim → authenticated but no DiVoid permissions → 403");
    }

    // -----------------------------------------------------------------------
    // Case 2b — Valid JWT, UserId claim uses old PascalCase name → 403
    //
    // Pins the contract: the system reads exactly the configured claim name
    // ("userId", camelCase). A token emitting "UserId" (PascalCase — the
    // previous wrong default) must be treated as if no user-id claim is
    // present, so no NameIdentifier / permission claims are emitted → 403.
    // -----------------------------------------------------------------------

    [Test]
    public async Task ValidJwt_PascalCaseUserIdClaim_Returns403()
    {
        long userId = await CreateEnabledUserAsync(
            permissionsJson: Json.WriteString(new[] { "read" }));

        // Deliberately mint with the old wrong claim name "UserId" (PascalCase).
        // The server is configured to look for "userId" (camelCase), so this
        // claim is invisible to KeycloakClaimsTransformation → no permissions → 403.
        string token = fixture.MintToken(userId: userId, userIdClaimName: "UserId");
        HttpResponseMessage response = await GetNodesAsync(token);

        Assert.That((int)response.StatusCode, Is.EqualTo(403),
            "JWT emitting 'UserId' (PascalCase) must return 403 — server expects 'userId' (camelCase)");
    }

    // -----------------------------------------------------------------------
    // Case 3 — Valid JWT, UserId present, no divoid_user row → 403
    // -----------------------------------------------------------------------

    [Test]
    public async Task ValidJwt_UnknownUserId_Returns403()
    {
        string token = fixture.MintToken(userId: 999999999L); // no such row in DB
        HttpResponseMessage response = await GetNodesAsync(token);

        Assert.That((int)response.StatusCode, Is.EqualTo(403),
            "valid JWT with non-existent DiVoid userId → no permission claims → 403");
    }

    // -----------------------------------------------------------------------
    // Case 4 — Valid JWT, UserId present, user disabled → 403
    // -----------------------------------------------------------------------

    [Test]
    public async Task ValidJwt_DisabledUser_Returns403()
    {
        long userId = await CreateDisabledUserAsync(
            permissionsJson: Json.WriteString(new[] { "read" }));
        string token = fixture.MintToken(userId: userId);
        HttpResponseMessage response = await GetNodesAsync(token);

        Assert.That((int)response.StatusCode, Is.EqualTo(403),
            "valid JWT for a disabled user → no permission claims emitted → 403");
    }

    // -----------------------------------------------------------------------
    // Case 5 — JWT signed by an unknown key → 401
    // -----------------------------------------------------------------------

    [Test]
    public async Task Jwt_WrongSigningKey_Returns401()
    {
        string token = fixture.MintToken(useWrongKey: true);
        HttpResponseMessage response = await GetNodesAsync(token);

        Assert.That((int)response.StatusCode, Is.EqualTo(401),
            "JWT signed by an unknown key must be 401 — signature validation fails");
    }

    // -----------------------------------------------------------------------
    // Case 6 — Expired JWT → 401
    // -----------------------------------------------------------------------

    [Test]
    public async Task Jwt_Expired_Returns401()
    {
        long userId = await CreateEnabledUserAsync(
            permissionsJson: Json.WriteString(new[] { "read" }));

        DateTime past  = DateTime.UtcNow.AddHours(-2);
        string   token = fixture.MintToken(
            userId:    userId,
            notBefore: past.AddMinutes(-5),
            expires:   past); // expired 2 h ago — well outside the 2-min clock skew

        HttpResponseMessage response = await GetNodesAsync(token);

        Assert.That((int)response.StatusCode, Is.EqualTo(401),
            "expired JWT must be 401");
    }

    // -----------------------------------------------------------------------
    // Case 7 — JWT with wrong audience → 401
    // -----------------------------------------------------------------------

    [Test]
    public async Task Jwt_WrongAudience_Returns401()
    {
        long userId = await CreateEnabledUserAsync(
            permissionsJson: Json.WriteString(new[] { "read" }));
        string token = fixture.MintToken(userId: userId, audience: "some-other-client");

        HttpResponseMessage response = await GetNodesAsync(token);

        Assert.That((int)response.StatusCode, Is.EqualTo(401),
            "JWT with wrong audience must be 401 — audience validation fails");
    }

    // -----------------------------------------------------------------------
    // Case 8 — Cross-scheme: ApiKey still authenticates alongside JwtBearer
    // -----------------------------------------------------------------------

    [Test]
    public async Task ApiKey_StillAuthenticatesWhenJwtBearerIsDefault()
    {
        long userId = await db.Insert<User>()
                              .Columns(u => u.Name, u => u.Enabled, u => u.CreatedAt)
                              .Values("apikey-regression-user", true, DateTime.UtcNow)
                              .ReturnID()
                              .ExecuteAsync();

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["DIVOID_KEY_PEPPER"] = JwtAuthFixture.TestPepper,
                ["Auth:Enabled"]      = "true"
            })
            .Build();

        ApiKeyService svc = new(
            db,
            new KeyGenerator(),
            config,
            NullLogger<ApiKeyService>.Instance);

        ApiKeyDetails key = await svc.CreateApiKey(new ApiKeyParameters {
            UserId      = userId,
            Permissions = ["read"]
        });

        HttpClient client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", key.PlaintextKey!);

        HttpResponseMessage response = await client.GetAsync("/api/nodes");

        Assert.That((int)response.StatusCode, Is.EqualTo(200),
            "API-key token must still authenticate successfully when JwtBearer is the default scheme");
    }

    // -----------------------------------------------------------------------
    // Case 9 — PolicyScheme: a JWT 403 must NOT emit "ApiKey was forbidden"
    //
    // Before the PolicyScheme refactor, policies listed both JwtBearer and ApiKey
    // as authentication schemes. The framework called ForbidAsync on every listed
    // scheme after a permission check failed, so a valid-but-unpermissioned JWT
    // caused both "JwtBearer was forbidden" AND "ApiKey was forbidden" to be logged.
    // This test pins the contract: under PolicyScheme dispatch only one scheme ever
    // runs per request, so ApiKey-related log lines must not appear on JWT 403s.
    // -----------------------------------------------------------------------

    [Test]
    public async Task Jwt_403Response_DoesNotEmit_ApiKeyForbiddenOrNotAuthenticated()
    {
        // Build a bespoke factory that replicates JwtAuthFixture's setup and adds a
        // capturing log provider. Startup.ConfigureServices calls ClearProviders();
        // the extra ConfigureServices delegate on WithWebHostBuilder runs after startup's
        // ConfigureServices, so the capturing provider is added after the clear and survives.
        CapturingLoggerProvider capturer = new();

        using JwtAuthWithCapturingLoggerFixture logFixture = new(capturer);

        // Insert a user with NO permissions — the JWT will validate successfully
        // (correct signature/audience/expiry) but the "read" policy will deny it → 403.
        long userId = await logFixture.EntityManager
            .Insert<User>()
            .Columns(u => u.Name, u => u.Email, u => u.Enabled, u => u.Permissions, u => u.CreatedAt)
            .Values("lognoise-user", "noise@test.com", true, Json.WriteString(Array.Empty<string>()), DateTime.UtcNow)
            .ReturnID()
            .ExecuteAsync();

        string token = logFixture.MintToken(userId: userId);

        HttpClient client = logFixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response = await client.GetAsync("/api/nodes");

        // Precondition: must produce 403 (authenticated but no permissions).
        Assert.That((int)response.StatusCode, Is.EqualTo(403),
            "valid JWT with no permissions must return 403 — precondition for the log-noise test");

        // The log must NOT contain ApiKey-related forbidden/unauthenticated messages.
        // Under PolicyScheme, the ApiKey scheme never runs for a JWT-shaped bearer;
        // the framework therefore never calls ForbidAsync on it.
        string allLogs = string.Join("\n", capturer.Messages);
        Assert.That(allLogs, Does.Not.Contain("ApiKey was forbidden"),
            "PolicyScheme must prevent 'ApiKey was forbidden' log noise on JWT 403 responses");
        Assert.That(allLogs, Does.Not.Contain("ApiKey was not authenticated"),
            "PolicyScheme must prevent 'ApiKey was not authenticated' log noise on JWT 403 responses");
    }
}

// ---------------------------------------------------------------------------
// Test infrastructure for log-noise test
// ---------------------------------------------------------------------------

/// <summary>
/// Variant of <see cref="JwtAuthFixture"/> that additionally registers a
/// <see cref="CapturingLoggerProvider"/> so tests can inspect log output.
/// Construction mirrors <see cref="JwtAuthFixture"/> exactly; the only
/// difference is the extra <c>ConfigureServices</c> call that adds the provider.
/// </summary>
file sealed class JwtAuthWithCapturingLoggerFixture : IDisposable
{
    readonly System.Security.Cryptography.RSA rsa;
    readonly Microsoft.IdentityModel.Tokens.RsaSecurityKey signingKey;
    readonly WebApplicationFactory<Program> factory;
    readonly System.Net.Http.HttpMessageHandler serverHandler;

    public JwtAuthWithCapturingLoggerFixture(CapturingLoggerProvider capturer)
    {
        rsa        = System.Security.Cryptography.RSA.Create(2048);
        signingKey = new Microsoft.IdentityModel.Tokens.RsaSecurityKey(rsa) { KeyId = "lognoise-key-1" };

        string jwks          = BuildJwks();
        string discoveryJson = BuildDiscoveryDocument();
        string dbPath        = $"/tmp/divoid_lognoise_{Guid.NewGuid():N}.db3";

        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => {
            builder.ConfigureAppConfiguration((_, config) => {
                config.AddInMemoryCollection(new Dictionary<string, string?> {
                    ["Auth:Enabled"]                  = "true",
                    ["DIVOID_KEY_PEPPER"]             = JwtAuthFixture.TestPepper,
                    ["Database:Type"]                 = "Sqlite",
                    ["Database:Source"]               = dbPath,
                    ["Keycloak:Authority"]            = JwtAuthFixture.TestAuthority,
                    ["Keycloak:Audience"]             = JwtAuthFixture.TestAudience,
                    ["Keycloak:RequireHttpsMetadata"] = "false",
                    ["Keycloak:UserIdClaimName"]      = "userId"
                });
            });
            builder.ConfigureServices(services => {
                // Override JwtBearer backchannel to serve canned JWKS (same pattern as JwtAuthFixture).
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options => {
                    options.BackchannelHttpHandler = new FakeJwksHandler(
                        JwtAuthFixture.TestAuthority, discoveryJson, jwks);
                    options.TokenValidationParameters.IssuerSigningKey  = signingKey;
                    options.TokenValidationParameters.IssuerSigningKeys = [signingKey];
                });
                // Add the capturing provider AFTER Startup's ClearProviders() has run.
                // Because this ConfigureServices runs after Startup.ConfigureServices, the
                // provider is appended after the clear and will receive log events.
                services.AddLogging(b => b.AddProvider(capturer));
            });
        });

        serverHandler = factory.Server.CreateHandler();
        EntityManager = factory.Services.GetRequiredService<Pooshit.Ocelot.Entities.IEntityManager>();
    }

    public Pooshit.Ocelot.Entities.IEntityManager EntityManager { get; }

    public System.Net.Http.HttpClient CreateClient() =>
        new(serverHandler) { BaseAddress = new Uri(TestSetup.BaseUrl) };

    public string MintToken(
        long?    userId   = null,
        string?  subject  = "lognoise-sub",
        string?  audience = JwtAuthFixture.TestAudience,
        string?  issuer   = JwtAuthFixture.TestAuthority)
    {
        DateTime now = DateTime.UtcNow;
        List<System.Security.Claims.Claim> claims = new();
        if (subject != null)  claims.Add(new System.Security.Claims.Claim(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub, subject));
        if (userId.HasValue)  claims.Add(new System.Security.Claims.Claim("userId", userId.Value.ToString()));

        Microsoft.IdentityModel.Tokens.SigningCredentials creds = new(
            signingKey, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha256);

        System.IdentityModel.Tokens.Jwt.JwtSecurityToken token = new(
            issuer:             issuer,
            audience:           audience,
            claims:             claims,
            notBefore:          now.AddSeconds(-5),
            expires:            now.AddMinutes(5),
            signingCredentials: creds);

        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }

    public void Dispose()
    {
        factory.Dispose();
        serverHandler.Dispose();
        rsa.Dispose();
    }

    string BuildJwks()
    {
        System.Security.Cryptography.RSAParameters p = rsa.ExportParameters(false);
        return System.Text.Json.JsonSerializer.Serialize(new {
            keys = new[] {
                new {
                    kty = "RSA",
                    use = "sig",
                    kid = signingKey.KeyId,
                    alg = "RS256",
                    n   = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(p.Modulus!),
                    e   = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(p.Exponent!)
                }
            }
        });
    }

    static string BuildDiscoveryDocument() =>
        System.Text.Json.JsonSerializer.Serialize(new {
            issuer                                  = JwtAuthFixture.TestAuthority,
            jwks_uri                                = $"{JwtAuthFixture.TestAuthority}/protocol/openid-connect/certs",
            authorization_endpoint                  = $"{JwtAuthFixture.TestAuthority}/protocol/openid-connect/auth",
            token_endpoint                          = $"{JwtAuthFixture.TestAuthority}/protocol/openid-connect/token",
            id_token_signing_alg_values_supported   = new[] { "RS256" }
        });

    sealed class FakeJwksHandler : System.Net.Http.HttpMessageHandler
    {
        readonly string authority;
        readonly string discoveryJson;
        readonly string jwksJson;

        public FakeJwksHandler(string authority, string discoveryJson, string jwksJson)
        {
            this.authority     = authority;
            this.discoveryJson = discoveryJson;
            this.jwksJson      = jwksJson;
        }

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken)
        {
            string url = request.RequestUri?.ToString() ?? "";
            if (url.Contains(".well-known/openid-configuration"))
                return Task.FromResult(JsonResponse(discoveryJson));
            if (url.Contains("/protocol/openid-connect/certs") ||
                (url.Contains("/certs") && url.StartsWith(authority, StringComparison.Ordinal)))
                return Task.FromResult(JsonResponse(jwksJson));
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound) {
                Content = new System.Net.Http.StringContent($"FakeJwksHandler: unexpected URL {url}")
            });
        }

        static System.Net.Http.HttpResponseMessage JsonResponse(string json) =>
            new(System.Net.HttpStatusCode.OK) {
                Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
    }
}

/// <summary>
/// Captures log messages from all categories for test assertions.
/// Thread-safe — uses a <see cref="ConcurrentBag{T}"/> internally.
/// </summary>
file sealed class CapturingLoggerProvider : ILoggerProvider
{
    readonly ConcurrentBag<string> messages = new();

    /// <summary>all captured log messages in the order they were received</summary>
    public IReadOnlyCollection<string> Messages => messages;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, messages);

    public void Dispose() { }

    sealed class CapturingLogger : ILogger
    {
        readonly string categoryName;
        readonly ConcurrentBag<string> messages;

        public CapturingLogger(string categoryName, ConcurrentBag<string> messages)
        {
            this.categoryName = categoryName;
            this.messages     = messages;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            messages.Add($"[{categoryName}] {formatter(state, exception)}");
        }
    }
}
