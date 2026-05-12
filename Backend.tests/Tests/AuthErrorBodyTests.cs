#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Backend.Models.Auth;
using Backend.Models.Users;
using Backend.Services.Auth;
using Backend.tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Pooshit.Json;
using Pooshit.Ocelot.Entities;

namespace Backend.tests.Tests;

/// <summary>
/// Integration tests that assert on the JSON error body returned by 401 and 403
/// responses from the authentication / authorization pipeline.
///
/// Every 401 must return the canonical project shape:
///   { "code": "authorization_invalidtoken", "text": "&lt;specific reason&gt;" }
///   Content-Type: application/json; charset=utf-8
///   WWW-Authenticate: Bearer
///
/// Every 403 must return:
///   { "code": "authorization_missingscope", "text": "Caller lacks required permission '...'" }
///   Content-Type: application/json; charset=utf-8
///
/// Positive paths (200) must not be affected.
/// </summary>
[TestFixture]
public class AuthErrorBodyTests
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

    Task<HttpResponseMessage> GetNodesWithTokenAsync(string token)
    {
        HttpClient client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.GetAsync("/api/nodes");
    }

    Task<HttpResponseMessage> GetNodesWithHeaderAsync(string header)
    {
        HttpClient client = fixture.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", header);
        return client.GetAsync("/api/nodes");
    }

    Task<HttpResponseMessage> GetNodesNoAuthAsync()
    {
        HttpClient client = fixture.CreateClient();
        return client.GetAsync("/api/nodes");
    }

    static async Task<ErrorBody> ReadErrorBodyAsync(HttpResponseMessage response)
    {
        string json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<ErrorBody>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
               ?? throw new InvalidOperationException($"Failed to deserialize error body: {json}");
    }

    static void AssertJsonContentType(HttpResponseMessage response)
    {
        string? ct = response.Content.Headers.ContentType?.ToString();
        Assert.That(ct, Does.Contain("application/json"),
            "error response Content-Type must be application/json");
    }

    static void AssertWwwAuthenticate(HttpResponseMessage response)
    {
        Assert.That(response.Headers.Contains("WWW-Authenticate"),
            "401 response must include WWW-Authenticate header");
        string wwwAuth = string.Join(", ", response.Headers.GetValues("WWW-Authenticate"));
        Assert.That(wwwAuth, Does.Contain("Bearer"),
            "WWW-Authenticate must use Bearer scheme");
    }

    async Task<long> CreateEnabledUserAsync(IEnumerable<string> permissions)
    {
        string permJson = Json.WriteString(permissions);
        return await db.Insert<User>()
                       .Columns(u => u.Name, u => u.Email, u => u.Enabled, u => u.Permissions, u => u.CreatedAt)
                       .Values($"err-body-user-{Guid.NewGuid():N}", "err@test.com", true, permJson, DateTime.UtcNow)
                       .ReturnID()
                       .ExecuteAsync();
    }

    async Task<long> CreateDisabledUserAsync()
    {
        return await db.Insert<User>()
                       .Columns(u => u.Name, u => u.Email, u => u.Enabled, u => u.Permissions, u => u.CreatedAt)
                       .Values($"err-body-disabled-{Guid.NewGuid():N}", "dis@test.com", false, Json.WriteString(new[] { "read" }), DateTime.UtcNow)
                       .ReturnID()
                       .ExecuteAsync();
    }

    // -----------------------------------------------------------------------
    // JWT 401 - wrong signing key -> "JWT signature could not be verified"
    // -----------------------------------------------------------------------

    [Test]
    public async Task Jwt_WrongSigningKey_Returns401WithSignatureDetail()
    {
        string token = fixture.MintToken(useWrongKey: true);
        HttpResponseMessage response = await GetNodesWithTokenAsync(token);

        Assert.That((int)response.StatusCode, Is.EqualTo(401));
        AssertJsonContentType(response);
        AssertWwwAuthenticate(response);

        ErrorBody body = await ReadErrorBodyAsync(response);
        Assert.Multiple(() => {
            Assert.That(body.Code, Is.EqualTo("authorization_invalidtoken"));
            Assert.That(body.Text, Is.EqualTo("JWT signature could not be verified"));
        });
    }

    // -----------------------------------------------------------------------
    // JWT 401 - expired token -> "JWT has expired"
    // -----------------------------------------------------------------------

    [Test]
    public async Task Jwt_Expired_Returns401WithExpiredDetail()
    {
        long userId = await CreateEnabledUserAsync(new[] { "read" });

        DateTime past  = DateTime.UtcNow.AddHours(-2);
        string   token = fixture.MintToken(
            userId:    userId,
            notBefore: past.AddMinutes(-5),
            expires:   past);

        HttpResponseMessage response = await GetNodesWithTokenAsync(token);

        Assert.That((int)response.StatusCode, Is.EqualTo(401));
        AssertJsonContentType(response);
        AssertWwwAuthenticate(response);

        ErrorBody body = await ReadErrorBodyAsync(response);
        Assert.Multiple(() => {
            Assert.That(body.Code, Is.EqualTo("authorization_invalidtoken"));
            Assert.That(body.Text, Is.EqualTo("JWT has expired"));
        });
    }

    // -----------------------------------------------------------------------
    // JWT 401 - wrong audience -> "JWT audience is not accepted by this service"
    // -----------------------------------------------------------------------

    [Test]
    public async Task Jwt_WrongAudience_Returns401WithAudienceDetail()
    {
        long userId = await CreateEnabledUserAsync(new[] { "read" });
        string token = fixture.MintToken(userId: userId, audience: "wrong-client");

        HttpResponseMessage response = await GetNodesWithTokenAsync(token);

        Assert.That((int)response.StatusCode, Is.EqualTo(401));
        AssertJsonContentType(response);
        AssertWwwAuthenticate(response);

        ErrorBody body = await ReadErrorBodyAsync(response);
        Assert.Multiple(() => {
            Assert.That(body.Code, Is.EqualTo("authorization_invalidtoken"));
            Assert.That(body.Text, Is.EqualTo("JWT audience is not accepted by this service"));
        });
    }

    // -----------------------------------------------------------------------
    // JWT 401 - wrong issuer -> "JWT issuer is not accepted by this service"
    // -----------------------------------------------------------------------

    [Test]
    public async Task Jwt_WrongIssuer_Returns401WithIssuerDetail()
    {
        long userId = await CreateEnabledUserAsync(new[] { "read" });
        string token = fixture.MintToken(userId: userId, issuer: "https://wrong-issuer.example.com/realms/other");

        HttpResponseMessage response = await GetNodesWithTokenAsync(token);

        Assert.That((int)response.StatusCode, Is.EqualTo(401));
        AssertJsonContentType(response);
        AssertWwwAuthenticate(response);

        ErrorBody body = await ReadErrorBodyAsync(response);
        Assert.Multiple(() => {
            Assert.That(body.Code, Is.EqualTo("authorization_invalidtoken"));
            Assert.That(body.Text, Is.EqualTo("JWT issuer is not accepted by this service"));
        });
    }

    // -----------------------------------------------------------------------
    // JWT 401 - malformed token -> "JWT could not be parsed"
    // -----------------------------------------------------------------------

    [Test]
    public async Task Jwt_Malformed_Returns401WithParseDetail()
    {
        // Two dots -> routes to JwtBearer (not ApiKey path), but not a valid JWT
        HttpResponseMessage response = await GetNodesWithTokenAsync("aaa.bbb.ccc");

        Assert.That((int)response.StatusCode, Is.EqualTo(401));
        AssertJsonContentType(response);
        AssertWwwAuthenticate(response);

        ErrorBody body = await ReadErrorBodyAsync(response);
        Assert.Multiple(() => {
            Assert.That(body.Code, Is.EqualTo("authorization_invalidtoken"));
            Assert.That(body.Text, Is.EqualTo("JWT could not be parsed"));
        });
    }

    // -----------------------------------------------------------------------
    // JWT 403 - authenticated but lacks permission
    // -----------------------------------------------------------------------

    [Test]
    public async Task Jwt_Authenticated_NoPermission_Returns403WithPermissionDetail()
    {
        // User exists and is enabled but has no permissions -> falls through to 403
        long userId = await CreateEnabledUserAsync(Array.Empty<string>());
        string token = fixture.MintToken(userId: userId);

        HttpResponseMessage response = await GetNodesWithTokenAsync(token);

        Assert.That((int)response.StatusCode, Is.EqualTo(403));
        AssertJsonContentType(response);

        ErrorBody body = await ReadErrorBodyAsync(response);
        Assert.Multiple(() => {
            Assert.That(body.Code, Is.EqualTo("authorization_missingscope"));
            // GET /api/nodes requires the "read" policy - pin the exact contract string
            Assert.That(body.Text, Is.EqualTo("Caller lacks required permission 'read'"));
        });
    }

    // -----------------------------------------------------------------------
    // JWT 200 - positive path: no error body injected
    // -----------------------------------------------------------------------

    [Test]
    public async Task Jwt_ValidToken_EnabledUser_Returns200()
    {
        long userId = await CreateEnabledUserAsync(new[] { "read" });
        string token = fixture.MintToken(userId: userId);

        HttpResponseMessage response = await GetNodesWithTokenAsync(token);

        Assert.That((int)response.StatusCode, Is.EqualTo(200),
            "valid JWT with read permission must return 200 - positive path must not be disrupted");
    }

    // -----------------------------------------------------------------------
    // ApiKey 401 - no Authorization header -> "Authorization header missing"
    // -----------------------------------------------------------------------

    [Test]
    public async Task NoAuthorizationHeader_Returns401WithMissingHeaderDetail()
    {
        HttpResponseMessage response = await GetNodesNoAuthAsync();

        Assert.That((int)response.StatusCode, Is.EqualTo(401));
        AssertJsonContentType(response);
        AssertWwwAuthenticate(response);

        ErrorBody body = await ReadErrorBodyAsync(response);
        Assert.Multiple(() => {
            Assert.That(body.Code, Is.EqualTo("authorization_invalidtoken"));
            Assert.That(body.Text, Is.EqualTo("Authorization header missing"));
        });
    }

    // -----------------------------------------------------------------------
    // ApiKey 401 - Authorization without Bearer prefix -> "Authorization header must use Bearer scheme"
    // -----------------------------------------------------------------------

    [Test]
    public async Task AuthorizationHeader_NonBearer_Returns401WithSchemeDetail()
    {
        HttpResponseMessage response = await GetNodesWithHeaderAsync("Basic dXNlcjpwYXNz");

        Assert.That((int)response.StatusCode, Is.EqualTo(401));
        AssertJsonContentType(response);
        AssertWwwAuthenticate(response);

        ErrorBody body = await ReadErrorBodyAsync(response);
        Assert.Multiple(() => {
            Assert.That(body.Code, Is.EqualTo("authorization_invalidtoken"));
            Assert.That(body.Text, Is.EqualTo("Authorization header must use Bearer scheme"));
        });
    }

    // -----------------------------------------------------------------------
    // ApiKey 401 - invalid/unknown API key -> "API key not recognised"
    // -----------------------------------------------------------------------

    [Test]
    public async Task ApiKey_InvalidKey_Returns401WithNotRecognisedDetail()
    {
        // A key in API-key format (single dot, no JWT) that does not exist in the DB
        HttpResponseMessage response = await GetNodesWithTokenAsync("00000000.invalidsecret");

        Assert.That((int)response.StatusCode, Is.EqualTo(401));
        AssertJsonContentType(response);
        AssertWwwAuthenticate(response);

        ErrorBody body = await ReadErrorBodyAsync(response);
        Assert.Multiple(() => {
            Assert.That(body.Code, Is.EqualTo("authorization_invalidtoken"));
            Assert.That(body.Text, Is.EqualTo("API key not recognised"));
        });
    }

    // -----------------------------------------------------------------------
    // ApiKey 401 - disabled key -> "API key is disabled"
    // -----------------------------------------------------------------------

    [Test]
    public async Task ApiKey_DisabledKey_Returns401WithDisabledKeyDetail()
    {
        long userId = await CreateEnabledUserAsync(new[] { "read" });

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

        // Disable the key directly via the entity manager
        await db.Update<ApiKey>()
                .Set(k => k.Enabled == false)
                .Where(k => k.Id == key.Id)
                .ExecuteAsync();

        HttpClient client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", key.PlaintextKey!);

        HttpResponseMessage response = await client.GetAsync("/api/nodes");

        Assert.That((int)response.StatusCode, Is.EqualTo(401));
        AssertJsonContentType(response);
        AssertWwwAuthenticate(response);

        ErrorBody body = await ReadErrorBodyAsync(response);
        Assert.Multiple(() => {
            Assert.That(body.Code, Is.EqualTo("authorization_invalidtoken"));
            Assert.That(body.Text, Is.EqualTo("API key is disabled"));
        });
    }

    // -----------------------------------------------------------------------
    // ApiKey 401 - expired key -> "API key has expired"
    // -----------------------------------------------------------------------

    [Test]
    public async Task ApiKey_ExpiredKey_Returns401WithExpiredDetail()
    {
        long userId = await CreateEnabledUserAsync(new[] { "read" });

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

        // Set expiry to 1 hour in the past
        DateTime expiredAt = DateTime.UtcNow.AddHours(-1);
        await db.Update<ApiKey>()
                .Set(k => k.ExpiresAt == expiredAt)
                .Where(k => k.Id == key.Id)
                .ExecuteAsync();

        HttpClient client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", key.PlaintextKey!);

        HttpResponseMessage response = await client.GetAsync("/api/nodes");

        Assert.That((int)response.StatusCode, Is.EqualTo(401));
        AssertJsonContentType(response);
        AssertWwwAuthenticate(response);

        ErrorBody body = await ReadErrorBodyAsync(response);
        Assert.Multiple(() => {
            Assert.That(body.Code, Is.EqualTo("authorization_invalidtoken"));
            Assert.That(body.Text, Is.EqualTo("API key has expired"));
        });
    }

    // -----------------------------------------------------------------------
    // ApiKey 401 - disabled DiVoid user -> "DiVoid account is disabled"
    // -----------------------------------------------------------------------

    [Test]
    public async Task ApiKey_DisabledUser_Returns401WithDisabledAccountDetail()
    {
        long userId = await CreateDisabledUserAsync();

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

        Assert.That((int)response.StatusCode, Is.EqualTo(401));
        AssertJsonContentType(response);
        AssertWwwAuthenticate(response);

        ErrorBody body = await ReadErrorBodyAsync(response);
        Assert.Multiple(() => {
            Assert.That(body.Code, Is.EqualTo("authorization_invalidtoken"));
            Assert.That(body.Text, Is.EqualTo("DiVoid account is disabled"));
        });
    }

    // -----------------------------------------------------------------------
    // ApiKey 200 - positive path: valid API key still returns 200
    // -----------------------------------------------------------------------

    [Test]
    public async Task ApiKey_ValidKey_Returns200()
    {
        long userId = await db.Insert<User>()
                              .Columns(u => u.Name, u => u.Enabled, u => u.CreatedAt)
                              .Values($"err-body-apikey-user-{Guid.NewGuid():N}", true, DateTime.UtcNow)
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
            "valid API key with read permission must return 200 - positive path must not be disrupted");
    }
}

/// <summary>
/// DTO for deserializing the canonical project error response body.
/// Matches <c>Pooshit.AspNetCore.Services.Errors.ErrorResponse</c> (<c>code</c> + <c>text</c>).
/// </summary>
sealed class ErrorBody
{
    public string Code { get; init; } = "";
    public string Text { get; init; } = "";
}
