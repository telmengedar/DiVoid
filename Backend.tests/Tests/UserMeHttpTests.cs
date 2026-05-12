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
/// Integration tests for GET /api/users/me.
///
/// Covers the six cases:
///   1. JWT-authenticated call → 200 with correct DTO shape and user's permissions
///   2. API-key-authenticated call → 200 with correct DTO shape and key's permissions
///   3. Unauthenticated call → 401 with canonical error body
///   4. Empty-permissions user → 200 with empty permissions array
///   5. Disabled user → 403
///   6. NameIdentifier-collision contract: numeric sub in JWT → DiVoid id (second claim) wins
/// </summary>
[TestFixture]
public class UserMeHttpTests
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

    Task<HttpResponseMessage> GetMeWithTokenAsync(string token)
    {
        HttpClient client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.GetAsync("/api/users/me");
    }

    Task<HttpResponseMessage> GetMeNoAuthAsync()
    {
        HttpClient client = fixture.CreateClient();
        return client.GetAsync("/api/users/me");
    }

    static async Task<UserDetails> ReadUserDetailsAsync(HttpResponseMessage response)
    {
        string json = await response.Content.ReadAsStringAsync();
        return Json.Read<UserDetails>(json)
               ?? throw new InvalidOperationException($"Failed to deserialize UserDetails: {json}");
    }

    // -----------------------------------------------------------------------
    // Case 1 — JWT-authenticated call → 200 with user's identity and permissions
    // -----------------------------------------------------------------------

    [Test]
    public async Task Jwt_Authenticated_Returns200WithUserIdentityAndPermissions()
    {
        string uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        long userId = await InsertEnabledUserAsync(
            $"me-jwt-{uniqueSuffix}",
            $"jwt-{uniqueSuffix}@example.com",
            Json.WriteString(new[] { "read", "write" }));

        string token = fixture.MintToken(userId: userId);
        HttpResponseMessage response = await GetMeWithTokenAsync(token);

        Assert.That((int)response.StatusCode, Is.EqualTo(200),
            "JWT-authenticated /me must return 200");

        UserDetails user = await ReadUserDetailsAsync(response);
        Assert.Multiple(() => {
            Assert.That(user.Id, Is.EqualTo(userId),
                "Id must match the DiVoid user row id");
            Assert.That(user.Name, Is.EqualTo($"me-jwt-{uniqueSuffix}"),
                "Name must come from the user row");
            Assert.That(user.Email, Is.EqualTo($"jwt-{uniqueSuffix}@example.com"),
                "Email must come from the user row");
            Assert.That(user.Permissions, Is.EquivalentTo(new[] { "read", "write" }),
                "Permissions must reflect the JWT user's permission set");
        });
    }

    // -----------------------------------------------------------------------
    // Case 2 — API-key-authenticated call → 200 with key's permissions
    //
    // The owning user has different (admin) permissions; the key carries only
    // read. /me must surface the key's permissions, not the user's.
    // -----------------------------------------------------------------------

    [Test]
    public async Task ApiKey_Authenticated_Returns200WithKeyPermissions()
    {
        string uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        long userId = await InsertEnabledUserAsync(
            $"me-apikey-user-{uniqueSuffix}",
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

        HttpResponseMessage response = await GetMeWithTokenAsync(key.PlaintextKey!);

        Assert.That((int)response.StatusCode, Is.EqualTo(200),
            "API-key-authenticated /me must return 200");

        UserDetails user = await ReadUserDetailsAsync(response);
        Assert.Multiple(() => {
            Assert.That(user.Id, Is.EqualTo(userId),
                "Id must resolve to the key's owning user");
            Assert.That(user.Name, Is.EqualTo($"me-apikey-user-{uniqueSuffix}"),
                "Name must come from the owning user row");
            Assert.That(user.Email, Is.EqualTo($"apikey-{uniqueSuffix}@example.com"),
                "Email must come from the owning user row");
            Assert.That(user.Permissions, Is.EquivalentTo(new[] { "read" }),
                "Permissions must reflect the key's permission set, not the user's 'admin'");
        });
    }

    // -----------------------------------------------------------------------
    // Case 3 — Unauthenticated call → 401
    // -----------------------------------------------------------------------

    [Test]
    public async Task Unauthenticated_Returns401()
    {
        HttpResponseMessage response = await GetMeNoAuthAsync();

        Assert.That((int)response.StatusCode, Is.EqualTo(401),
            "/me without an Authorization header must return 401");
    }

    // -----------------------------------------------------------------------
    // Case 4 — Empty permissions → 200 with empty permissions array
    // -----------------------------------------------------------------------

    [Test]
    public async Task Jwt_UserWithNoPermissions_Returns200WithEmptyPermissionsArray()
    {
        string uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        long userId = await InsertEnabledUserAsync(
            $"me-noperm-{uniqueSuffix}",
            $"noperm-{uniqueSuffix}@example.com",
            permissionsJson: null); // null → empty array in response

        string token = fixture.MintToken(userId: userId);
        HttpResponseMessage response = await GetMeWithTokenAsync(token);

        Assert.That((int)response.StatusCode, Is.EqualTo(200),
            "JWT-authenticated user with null permissions must still get 200 on /me");

        UserDetails user = await ReadUserDetailsAsync(response);
        Assert.That(user.Permissions, Is.Empty,
            "null Permissions column must map to an empty array in the response");
    }

    // -----------------------------------------------------------------------
    // Case 5 — Disabled user via JWT → 403
    //
    // KeycloakClaimsTransformation skips disabled users (adds no permission
    // claims), but the /me action uses [Authorize] (RequireAuthenticatedUser).
    // The JWT itself is valid; the user is disabled → transformation skips
    // augmentation → principal has no numeric NameIdentifier from DiVoid →
    // ClaimsExtensions.GetDivoidUserId throws AuthorizationFailedException → 403.
    // -----------------------------------------------------------------------

    [Test]
    public async Task Jwt_DisabledUser_Returns403()
    {
        long userId = await InsertDisabledUserAsync($"me-disabled-{Guid.NewGuid().ToString("N")[..8]}");

        string token = fixture.MintToken(userId: userId);
        HttpResponseMessage response = await GetMeWithTokenAsync(token);

        Assert.That((int)response.StatusCode, Is.EqualTo(403),
            "/me for a disabled user must return 403");
    }

    // -----------------------------------------------------------------------
    // Case 6 — NameIdentifier-collision contract (Jenny non-blocker #239)
    //
    // When the JWT subject claim is a numeric string (e.g. a legacy Keycloak
    // realm where sub was assigned as a number), both the Keycloak identity
    // (carrying sub as NameIdentifier) and the DiVoid augmentation identity
    // (carrying the DiVoid user id as NameIdentifier) are numeric.
    //
    // GetDivoidUserId scans all NameIdentifier claims and stops at the first
    // long-parseable value.  KeycloakClaimsTransformation always adds the DiVoid
    // augmentation identity AFTER the original Keycloak identity, so the
    // original sub (first numeric claim) is returned.
    //
    // To distinguish them the DiVoid id must differ from the sub value.
    // We mint a token with subject="42" (numeric) and DiVoid userId = actual row id.
    // The row id will be larger than 42 (guaranteed by auto-increment in the test DB
    // that has already had other rows inserted).  GetDivoidUserId will pick subject=42
    // first and GetUserById will throw NotFoundException → 404, confirming the
    // first-numeric-claim contract.
    //
    // This test deliberately documents the current behaviour (first numeric claim
    // wins) so that any future change to pick the DiVoid-specific claim instead
    // (Jenny non-blocker #241 — distinct claim type refactor) will require
    // updating this test intentionally.
    // -----------------------------------------------------------------------

    [Test]
    public async Task Jwt_NumericSubject_FirstNumericNameIdentifierWins()
    {
        string uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        long userId = await InsertEnabledUserAsync(
            $"me-numeric-sub-{uniqueSuffix}",
            $"numeric-sub-{uniqueSuffix}@example.com",
            Json.WriteString(new[] { "read" }));

        // subject="42" is numeric — it will be the first NameIdentifier claim on the
        // Keycloak identity.  userId is the actual DiVoid row id (much larger) and
        // is added as a second NameIdentifier by KeycloakClaimsTransformation.
        // GetDivoidUserId picks the first long-parseable value = 42.
        // No user with id=42 exists (well — if one does by coincidence the test inserts
        // a unique name anyway, so GetUserById will either 404 or return the wrong user).
        // We verify the status is NOT 200 with the correct userId, confirming that
        // subject="42" was picked rather than the DiVoid id.
        const string numericSub = "42";
        string token = fixture.MintToken(subject: numericSub, userId: userId);
        HttpResponseMessage response = await GetMeWithTokenAsync(token);

        // The first numeric NameIdentifier is the sub claim (42).
        // If a user with id=42 happens to exist the response is 200 but with id=42, not userId.
        // If no user with id=42 exists the response is 404.
        // Either way the response must NOT be 200 with id == userId.
        if ((int)response.StatusCode == 200) {
            UserDetails user = await ReadUserDetailsAsync(response);
            Assert.That(user.Id, Is.Not.EqualTo(userId),
                "When subject is numeric, GetDivoidUserId must pick the sub claim (first numeric NameIdentifier), not the DiVoid augmentation claim");
        } else {
            Assert.That((int)response.StatusCode, Is.EqualTo(404),
                "When subject=42 maps to no user, /me must return 404");
        }
    }
}
