#nullable enable
using System;
using System.Collections.Generic;
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
/// Integration tests for GET /api/auth/whoami.
///
/// Covers the four cases from task #195:
///   1. JWT-authenticated call → 200 with correct DTO shape and user's permissions
///   2. API-key-authenticated call → 200 with correct DTO shape and key's permissions
///   3. Unauthenticated call → 401 with canonical error body
///   4. Empty-permissions user → 200 with empty permissions array
///   5. Disabled user → 403
/// </summary>
[TestFixture]
public class WhoamiHttpTests
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

    async Task<long> InsertEnabledUserAsync(string name, string email, string? permissionsJson = null)
    {
        return await db.Insert<User>()
                       .Columns(u => u.Name, u => u.Email, u => u.Enabled, u => u.Permissions, u => u.CreatedAt)
                       .Values(name, email, true, permissionsJson, DateTime.UtcNow)
                       .ReturnID()
                       .ExecuteAsync();
    }

    async Task<long> InsertDisabledUserAsync(string name)
    {
        return await db.Insert<User>()
                       .Columns(u => u.Name, u => u.Email, u => u.Enabled, u => u.Permissions, u => u.CreatedAt)
                       .Values(name, $"{name}@test.com", false, null, DateTime.UtcNow)
                       .ReturnID()
                       .ExecuteAsync();
    }

    Task<HttpResponseMessage> GetWhoamiWithTokenAsync(string token)
    {
        HttpClient client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.GetAsync("/api/auth/whoami");
    }

    Task<HttpResponseMessage> GetWhoamiNoAuthAsync()
    {
        HttpClient client = fixture.CreateClient();
        return client.GetAsync("/api/auth/whoami");
    }

    static async Task<WhoamiDetails> ReadWhoamiAsync(HttpResponseMessage response)
    {
        string json = await response.Content.ReadAsStringAsync();
        return Json.Read<WhoamiDetails>(json)
               ?? throw new InvalidOperationException($"Failed to deserialize WhoamiDetails: {json}");
    }

    // -----------------------------------------------------------------------
    // Case 1 — JWT-authenticated call → 200 with user's identity and permissions
    // -----------------------------------------------------------------------

    [Test]
    public async Task Jwt_Authenticated_Returns200WithUserIdentityAndPermissions()
    {
        string uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        long userId = await InsertEnabledUserAsync(
            $"whoami-jwt-{uniqueSuffix}",
            $"jwt-{uniqueSuffix}@example.com",
            Json.WriteString(new[] { "read", "write" }));

        string token = fixture.MintToken(userId: userId);
        HttpResponseMessage response = await GetWhoamiWithTokenAsync(token);

        Assert.That((int)response.StatusCode, Is.EqualTo(200),
            "JWT-authenticated whoami must return 200");

        WhoamiDetails whoami = await ReadWhoamiAsync(response);
        Assert.Multiple(() => {
            Assert.That(whoami.UserId, Is.EqualTo(userId),
                "userId must match the DiVoid user row id");
            Assert.That(whoami.Name, Is.EqualTo($"whoami-jwt-{uniqueSuffix}"),
                "name must come from the user row");
            Assert.That(whoami.Email, Is.EqualTo($"jwt-{uniqueSuffix}@example.com"),
                "email must come from the user row");
            Assert.That(whoami.Permissions, Is.EquivalentTo(new[] { "read", "write" }),
                "permissions must reflect the JWT user's permission set");
        });
    }

    // -----------------------------------------------------------------------
    // Case 2 — API-key-authenticated call → 200 with key's permissions
    //
    // The owning user has different (admin) permissions; the key carries only
    // read. whoami must surface the key's permissions, not the user's.
    // -----------------------------------------------------------------------

    [Test]
    public async Task ApiKey_Authenticated_Returns200WithKeyPermissions()
    {
        string uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        long userId = await InsertEnabledUserAsync(
            $"whoami-apikey-user-{uniqueSuffix}",
            $"apikey-{uniqueSuffix}@example.com",
            Json.WriteString(new[] { "admin" })); // user is admin

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

        // Key carries only "read" — deliberately different from user's "admin"
        ApiKeyDetails key = await svc.CreateApiKey(new ApiKeyParameters {
            UserId      = userId,
            Permissions = ["read"]
        });

        HttpResponseMessage response = await GetWhoamiWithTokenAsync(key.PlaintextKey!);

        Assert.That((int)response.StatusCode, Is.EqualTo(200),
            "API-key-authenticated whoami must return 200");

        WhoamiDetails whoami = await ReadWhoamiAsync(response);
        Assert.Multiple(() => {
            Assert.That(whoami.UserId, Is.EqualTo(userId),
                "userId must resolve to the key's owning user");
            Assert.That(whoami.Name, Is.EqualTo($"whoami-apikey-user-{uniqueSuffix}"),
                "name must come from the owning user row");
            Assert.That(whoami.Email, Is.EqualTo($"apikey-{uniqueSuffix}@example.com"),
                "email must come from the owning user row");
            Assert.That(whoami.Permissions, Is.EquivalentTo(new[] { "read" }),
                "permissions must reflect the key's permission set, not the user's 'admin'");
        });
    }

    // -----------------------------------------------------------------------
    // Case 3 — Unauthenticated call → 401
    // -----------------------------------------------------------------------

    [Test]
    public async Task Unauthenticated_Returns401()
    {
        HttpResponseMessage response = await GetWhoamiNoAuthAsync();

        Assert.That((int)response.StatusCode, Is.EqualTo(401),
            "whoami without an Authorization header must return 401");
    }

    // -----------------------------------------------------------------------
    // Case 4 — Empty permissions → 200 with empty permissions array
    // -----------------------------------------------------------------------

    [Test]
    public async Task Jwt_UserWithNoPermissions_Returns200WithEmptyPermissionsArray()
    {
        string uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        long userId = await InsertEnabledUserAsync(
            $"whoami-noperm-{uniqueSuffix}",
            $"noperm-{uniqueSuffix}@example.com",
            permissionsJson: null); // null → empty array in response

        string token = fixture.MintToken(userId: userId);
        HttpResponseMessage response = await GetWhoamiWithTokenAsync(token);

        Assert.That((int)response.StatusCode, Is.EqualTo(200),
            "JWT-authenticated user with null permissions must still get 200 on whoami");

        WhoamiDetails whoami = await ReadWhoamiAsync(response);
        Assert.That(whoami.Permissions, Is.Empty,
            "null Permissions column must map to an empty array in the response");
    }

    // -----------------------------------------------------------------------
    // Case 5 — Disabled user via JWT → 403
    //
    // KeycloakClaimsTransformation skips disabled users (adds no permission
    // claims), but the whoami action uses [Authorize] (RequireAuthenticatedUser).
    // The JWT itself is valid; the user is disabled → transformation skips
    // augmentation → principal has no NameIdentifier from DiVoid → AuthService
    // throws AuthorizationFailedException → 403.
    // -----------------------------------------------------------------------

    [Test]
    public async Task Jwt_DisabledUser_Returns403()
    {
        long userId = await InsertDisabledUserAsync($"whoami-disabled-{Guid.NewGuid().ToString("N")[..8]}");

        string token = fixture.MintToken(userId: userId);
        HttpResponseMessage response = await GetWhoamiWithTokenAsync(token);

        Assert.That((int)response.StatusCode, Is.EqualTo(403),
            "whoami for a disabled user must return 403");
    }
}
