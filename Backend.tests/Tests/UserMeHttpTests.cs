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
    // augmentation → principal has no "divoid.user_id" claim from DiVoid →
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
    // Case 6 - divoid.user_id claim contract (DiVoid #240)
    //
    // After the refactor, KeycloakClaimsTransformation emits a distinct
    // "divoid.user_id" claim (not a second NameIdentifier).  GetDivoidUserId
    // reads that claim directly.  This means that even when the JWT subject is
    // a numeric string (a Keycloak configuration that would previously have
    // caused the scan to pick the wrong NameIdentifier claim), /me correctly
    // identifies the DiVoid user via the unambiguous "divoid.user_id" claim.
    //
    // Substitution probe: comment out the divoid.user_id AddClaim call in
    // KeycloakClaimsTransformation - GetDivoidUserId throws
    // AuthorizationFailedException - /me returns 403 instead of 200 - fails.
    // -----------------------------------------------------------------------

    [Test]
    public async Task Jwt_NumericSubject_DivoidUserIdClaimAlwaysResolvesCorrectUser()
    {
        string uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        long userId = await InsertEnabledUserAsync(
            $"me-numeric-sub-{uniqueSuffix}",
            $"numeric-sub-{uniqueSuffix}@example.com",
            Json.WriteString(new[] { "read" }));

        // subject="42" is numeric - this would have confused the old NameIdentifier
        // scan, but the refactor emits a distinct "divoid.user_id" claim that
        // GetDivoidUserId reads directly.  The response must always resolve to
        // the correct DiVoid user regardless of the sub value.
        const string numericSub = "42";
        string token = fixture.MintToken(subject: numericSub, userId: userId);
        HttpResponseMessage response = await GetMeWithTokenAsync(token);

        Assert.That((int)response.StatusCode, Is.EqualTo(200),
            "JWT with numeric sub must still return 200 - divoid.user_id claim is unambiguous");

        UserDetails user = await ReadUserDetailsAsync(response);
        Assert.That(user.Id, Is.EqualTo(userId),
            "divoid.user_id claim must resolve to the correct DiVoid user id, not the numeric sub");
    }
}