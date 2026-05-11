#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
}
