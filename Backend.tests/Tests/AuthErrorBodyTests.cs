#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
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
/// Every 401 must return:
///   { "status": 401, "title": "Unauthorized", "detail": "&lt;specific reason&gt;" }
///   Content-Type: application/json; charset=utf-8
///   WWW-Authenticate: Bearer
///
/// Every 403 must return:
///   { "status": 403, "title": "Forbidden", "detail": "Caller lacks required permission '...'" }
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
    // JWT 401 — wrong signing key → "JWT signature could not be verified"
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
            Assert.That(body.Status, Is.EqualTo(401));
            Assert.That(body.Title, Is.EqualTo("Unauthorized"));
            Assert.That(body.Detail, Is.EqualTo("JWT signature could not be verified"));
        });
    }

    // -----------------------------------------------------------------------
    // JWT 401 — expired token → "JWT has expired"
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
            Assert.That(body.Status, Is.EqualTo(401));
            Assert.That(body.Title, Is.EqualTo("Unauthorized"));
            Assert.That(body.Detail, Is.EqualTo("JWT has expired"));
        });
    }

    // -----------------------------------------------------------------------
    // JWT 401 — wrong audience → "JWT audience is not accepted by this service"
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
            Assert.That(body.Status, Is.EqualTo(401));
            Assert.That(body.Title, Is.EqualTo("Unauthorized"));
            Assert.That(body.Detail, Is.EqualTo("JWT audience is not accepted by this service"));
        });
    }

    // -----------------------------------------------------------------------
    // JWT 403 — authenticated but lacks permission
    // -----------------------------------------------------------------------

    [Test]
    public async Task Jwt_Authenticated_NoPermission_Returns403WithPermissionDetail()
    {
        // User exists and is enabled but has no permissions → falls through to 403
        long userId = await CreateEnabledUserAsync(Array.Empty<string>());
        string token = fixture.MintToken(userId: userId);

        HttpResponseMessage response = await GetNodesWithTokenAsync(token);

        Assert.That((int)response.StatusCode, Is.EqualTo(403));
        AssertJsonContentType(response);

        ErrorBody body = await ReadErrorBodyAsync(response);
        Assert.Multiple(() => {
            Assert.That(body.Status, Is.EqualTo(403));
            Assert.That(body.Title, Is.EqualTo("Forbidden"));
            // GET /api/nodes requires the "read" policy
            Assert.That(body.Detail, Does.Contain("read"));
        });
    }

    // -----------------------------------------------------------------------
    // JWT 200 — positive path: no error body injected
    // -----------------------------------------------------------------------

    [Test]
    public async Task Jwt_ValidToken_EnabledUser_Returns200()
    {
        long userId = await CreateEnabledUserAsync(new[] { "read" });
        string token = fixture.MintToken(userId: userId);

        HttpResponseMessage response = await GetNodesWithTokenAsync(token);

        Assert.That((int)response.StatusCode, Is.EqualTo(200),
            "valid JWT with read permission must return 200 — positive path must not be disrupted");
    }

    // -----------------------------------------------------------------------
    // ApiKey 401 — no Authorization header → "Authorization header missing"
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
            Assert.That(body.Status, Is.EqualTo(401));
            Assert.That(body.Title, Is.EqualTo("Unauthorized"));
            Assert.That(body.Detail, Is.EqualTo("Authorization header missing"));
        });
    }

    // -----------------------------------------------------------------------
    // ApiKey 401 — Authorization without Bearer prefix → "Authorization header must use Bearer scheme"
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
            Assert.That(body.Status, Is.EqualTo(401));
            Assert.That(body.Title, Is.EqualTo("Unauthorized"));
            Assert.That(body.Detail, Is.EqualTo("Authorization header must use Bearer scheme"));
        });
    }

    // -----------------------------------------------------------------------
    // ApiKey 401 — invalid/unknown API key → "API key not recognised"
    // -----------------------------------------------------------------------

    [Test]
    public async Task ApiKey_InvalidKey_Returns401WithNotRecognisedDetail()
    {
        // A key in API-key format (single dot, no JWT) that doesn't exist in the DB
        HttpResponseMessage response = await GetNodesWithTokenAsync("00000000.invalidsecret");

        Assert.That((int)response.StatusCode, Is.EqualTo(401));
        AssertJsonContentType(response);
        AssertWwwAuthenticate(response);

        ErrorBody body = await ReadErrorBodyAsync(response);
        Assert.Multiple(() => {
            Assert.That(body.Status, Is.EqualTo(401));
            Assert.That(body.Title, Is.EqualTo("Unauthorized"));
            Assert.That(body.Detail, Is.EqualTo("API key not recognised"));
        });
    }

    // -----------------------------------------------------------------------
    // ApiKey 200 — positive path: valid API key still returns 200
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
            "valid API key with read permission must return 200 — positive path must not be disrupted");
    }
}

/// <summary>
/// DTO for deserializing the three-field auth error response body.
/// </summary>
sealed class ErrorBody
{
    public int Status { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
}
