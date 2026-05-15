#nullable enable
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Backend.Models.Users;
using Backend.tests.Fixtures;
using NUnit.Framework;
using Pooshit.Json;
using Pooshit.Ocelot.Entities;

namespace Backend.tests.Tests;

/// <summary>
/// Integration tests for User.HomeNodeId + PATCH /api/users/me (task #398).
///
/// Seven load-bearing cases:
///   1. HomeNodeId round-trips on GET /api/users/me
///   2. HomeNodeId is null for users who have not set one
///   3. PATCH /api/users/me with homeNodeId updates the column
///   4. PATCH /api/users/me rejects patches to non-allowlisted properties
///   5. PATCH /api/users/me patches only the authenticated user, not someone else
///   6. Admin PATCH /api/users/{id} regression — admin route still works
///   7. Non-authenticated PATCH /api/users/me returns 401
/// </summary>
[TestFixture]
public class UserMeHomeNodeHttpTests
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

    async Task<long> InsertUserAsync(string name, long? homeNodeId = null)
    {
        return await db.Insert<User>()
                       .Columns(u => u.Name, u => u.Email, u => u.Enabled, u => u.Permissions, u => u.CreatedAt, u => u.HomeNodeId)
                       .Values(name, $"{name}@test.com", true, null, DateTime.UtcNow, homeNodeId)
                       .ReturnID()
                       .ExecuteAsync();
    }

    HttpClient AuthedClient(string token)
    {
        HttpClient client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    HttpClient AnonClient() => fixture.CreateClient();

    static async Task<UserDetails> ReadUserDetailsAsync(HttpResponseMessage response)
    {
        string json = await response.Content.ReadAsStringAsync();
        return Json.Read<UserDetails>(json)
               ?? throw new InvalidOperationException($"Failed to deserialize UserDetails: {json}");
    }

    static StringContent JsonPatch(string body) =>
        new(body, Encoding.UTF8, "application/json");

    // -----------------------------------------------------------------------
    // Case 1 — HomeNodeId round-trips on GET /api/users/me
    //
    // Negative substitution: drop HomeNodeId from UserDetails → the field is
    // absent in JSON → Json.Read gives default 0, not 42 → assertion fails.
    // -----------------------------------------------------------------------

    [Test]
    public async Task GetMe_HomeNodeIdSeeded_RoundTripsInResponse()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long userId = await InsertUserAsync($"homenodeid-rt-{suffix}", homeNodeId: 42);

        string token = fixture.MintToken(userId: userId);
        using HttpClient client = AuthedClient(token);
        HttpResponseMessage response = await client.GetAsync("/api/users/me");

        Assert.That((int)response.StatusCode, Is.EqualTo(200),
            "GET /me for user with HomeNodeId must return 200");

        UserDetails user = await ReadUserDetailsAsync(response);
        Assert.That(user.HomeNodeId, Is.EqualTo(42L),
            "HomeNodeId must round-trip from the DB through the DTO to the response");
    }

    // -----------------------------------------------------------------------
    // Case 2 — HomeNodeId is null for users who have not set one
    //
    // Negative substitution: change UserDetails.HomeNodeId to long (non-nullable)
    // → default value 0 is serialised → assertion for null fails.
    // -----------------------------------------------------------------------

    [Test]
    public async Task GetMe_HomeNodeIdNotSet_ReturnsNull()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long userId = await InsertUserAsync($"homenodeid-null-{suffix}", homeNodeId: null);

        string token = fixture.MintToken(userId: userId);
        using HttpClient client = AuthedClient(token);
        HttpResponseMessage response = await client.GetAsync("/api/users/me");

        Assert.That((int)response.StatusCode, Is.EqualTo(200),
            "GET /me for user without HomeNodeId must return 200");

        UserDetails user = await ReadUserDetailsAsync(response);
        Assert.That(user.HomeNodeId, Is.Null,
            "HomeNodeId must be null in the response when the column has not been set");
    }

    // -----------------------------------------------------------------------
    // Case 3 — PATCH /api/users/me with homeNodeId updates the column
    //
    // Negative substitution: remove [AllowPatch] from User.HomeNodeId → Patch()
    // throws NotSupportedException → middleware maps it to 400/500, not 200
    // → the follow-up GET returns the old null, assertion fails.
    // -----------------------------------------------------------------------

    [Test]
    public async Task PatchMe_HomeNodeId_UpdatesColumn()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long userId = await InsertUserAsync($"homenodeid-patch-{suffix}", homeNodeId: null);

        string token = fixture.MintToken(userId: userId);
        using HttpClient client = AuthedClient(token);

        // PATCH homeNodeId to 10
        string patchBody = "[{\"op\":\"replace\",\"path\":\"/homeNodeId\",\"value\":10}]";
        HttpResponseMessage patchResp = await client.PatchAsync("/api/users/me", JsonPatch(patchBody));
        Assert.That((int)patchResp.StatusCode, Is.EqualTo(200),
            $"PATCH /me with homeNodeId must return 200; body: {await patchResp.Content.ReadAsStringAsync()}");

        // Confirm the value persisted
        HttpResponseMessage getResp = await client.GetAsync("/api/users/me");
        UserDetails user = await ReadUserDetailsAsync(getResp);
        Assert.That(user.HomeNodeId, Is.EqualTo(10L),
            "HomeNodeId must be 10 after the PATCH");
    }

    // -----------------------------------------------------------------------
    // Case 4 — PATCH /api/users/me rejects patches to non-allowlisted properties
    //
    // Patching a property that does not carry [AllowPatch] (e.g. Name, which is
    // not decorated) must be rejected — the allowlist is the security boundary.
    //
    // Negative substitution: this test is itself the boundary check. If it
    // starts returning 200, the allowlist has been weakened.
    // -----------------------------------------------------------------------

    [Test]
    public async Task PatchMe_NonAllowlistedProperty_IsRejected()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long userId = await InsertUserAsync($"homenodeid-allowlist-{suffix}");

        string token = fixture.MintToken(userId: userId);
        using HttpClient client = AuthedClient(token);

        // Name does not carry [AllowPatch] → must be rejected
        string patchBody = "[{\"op\":\"replace\",\"path\":\"/name\",\"value\":\"hacked\"}]";
        HttpResponseMessage patchResp = await client.PatchAsync("/api/users/me", JsonPatch(patchBody));

        Assert.That((int)patchResp.StatusCode, Is.Not.EqualTo(200),
            "PATCH /me for a non-[AllowPatch] property must not return 200");
    }

    // -----------------------------------------------------------------------
    // Case 5 — PATCH /api/users/me patches only the authenticated user
    //
    // Two users are seeded. User A authenticates and sends PATCH /me. Only
    // user A's row must change; user B must remain unaffected.
    //
    // Negative substitution: replace User.GetDivoidUserId() call with a
    // hardcoded id → wrong user row is updated → assertion on user A fails.
    // -----------------------------------------------------------------------

    [Test]
    public async Task PatchMe_PatchesOnlyAuthenticatedUser()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long userAId = await InsertUserAsync($"homenodeid-userA-{suffix}", homeNodeId: null);
        long userBId = await InsertUserAsync($"homenodeid-userB-{suffix}", homeNodeId: null);

        string tokenA = fixture.MintToken(userId: userAId);
        using HttpClient clientA = AuthedClient(tokenA);

        string patchBody = "[{\"op\":\"replace\",\"path\":\"/homeNodeId\",\"value\":99}]";
        HttpResponseMessage patchResp = await clientA.PatchAsync("/api/users/me", JsonPatch(patchBody));
        Assert.That((int)patchResp.StatusCode, Is.EqualTo(200),
            $"PATCH /me as user A must return 200; body: {await patchResp.Content.ReadAsStringAsync()}");

        // User A must have HomeNodeId = 99
        HttpResponseMessage getA = await clientA.GetAsync("/api/users/me");
        UserDetails userA = await ReadUserDetailsAsync(getA);
        Assert.That(userA.HomeNodeId, Is.EqualTo(99L),
            "User A must have HomeNodeId = 99 after the PATCH");

        // User B must remain unaffected (HomeNodeId still null)
        // Use a direct DB read to avoid needing admin credentials for /api/users/{id}
        User? rawB = await db.Load<User>()
                             .Where(u => u.Id == userBId)
                             .ExecuteEntityAsync();
        Assert.That(rawB, Is.Not.Null, "User B row must still exist");
        Assert.That(rawB!.HomeNodeId, Is.Null,
            "User B's HomeNodeId must remain null — PATCH /me must not touch other users");
    }

    // -----------------------------------------------------------------------
    // Case 6 — Admin PATCH /api/users/{id} regression — admin route still works
    //
    // Adding the /me route must not break the existing admin route.
    //
    // Negative substitution: accidentally remove the admin route or change its
    // auth policy → this test catches either regression.
    // -----------------------------------------------------------------------

    [Test]
    public async Task AdminPatchUser_HomeNodeId_StillWorks()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long targetId = await InsertUserAsync($"homenodeid-admin-target-{suffix}", homeNodeId: null);

        // Admin JWT (admin permission granted via fixture token carrying the claim)
        // The JwtAuthFixture fixture uses Auth:Enabled=true; admin permissions
        // are enforced by the PermissionAuthorizationHandler reading the "permission"
        // claims. We seed an admin user and mint a JWT that carries the admin claim.
        long adminId = await db.Insert<User>()
                               .Columns(u => u.Name, u => u.Email, u => u.Enabled, u => u.Permissions, u => u.CreatedAt)
                               .Values($"admin-{suffix}", $"admin-{suffix}@test.com", true,
                                       Pooshit.Json.Json.WriteString(new[] { "admin" }), DateTime.UtcNow)
                               .ReturnID()
                               .ExecuteAsync();

        string adminToken = fixture.MintToken(userId: adminId);
        using HttpClient adminClient = AuthedClient(adminToken);

        string patchBody = "[{\"op\":\"replace\",\"path\":\"/homeNodeId\",\"value\":77}]";
        HttpResponseMessage patchResp = await adminClient.PatchAsync($"/api/users/{targetId}", JsonPatch(patchBody));
        Assert.That((int)patchResp.StatusCode, Is.EqualTo(200),
            $"Admin PATCH /api/users/{{id}} must still return 200; body: {await patchResp.Content.ReadAsStringAsync()}");

        UserDetails target = await ReadUserDetailsAsync(patchResp);
        Assert.That(target.HomeNodeId, Is.EqualTo(77L),
            "Admin PATCH /api/users/{id} must update HomeNodeId on the target user");
    }

    // -----------------------------------------------------------------------
    // Case 7 — Non-authenticated PATCH /api/users/me returns 401
    //
    // Negative substitution: remove [Authorize] from PatchMe → the endpoint
    // accepts anonymous requests → status is 200/400 instead of 401 → fails.
    // -----------------------------------------------------------------------

    [Test]
    public async Task PatchMe_Unauthenticated_Returns401()
    {
        using HttpClient client = AnonClient();
        string patchBody = "[{\"op\":\"replace\",\"path\":\"/homeNodeId\",\"value\":1}]";
        HttpResponseMessage response = await client.PatchAsync("/api/users/me", JsonPatch(patchBody));

        Assert.That((int)response.StatusCode, Is.EqualTo(401),
            "PATCH /api/users/me without an Authorization header must return 401");
    }
}
