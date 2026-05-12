#nullable enable
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Backend.Models.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.Json;
using Pooshit.Ocelot.Entities;

namespace Backend.tests.Tests;

/// <summary>
/// Integration tests for User.Permissions surfacing:
///   - GET /api/users/{id} returns Permissions as string[] (not raw JSON string)
///   - GET /api/users list returns Permissions per user
///   - PATCH /api/users/{id} accepts the natural array shape
///   - PATCH /api/users/{id} rejects non-array values (string, number) with 400
/// </summary>
[TestFixture]
public class UserPermissionsTests
{
    WebApplicationFactory<Program> factory = null!;
    IEntityManager db = null!;
    HttpClient client = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        factory = TestSetup.CreateTestFactory();
        db      = factory.Services.GetService(typeof(IEntityManager)) as IEntityManager
                  ?? throw new InvalidOperationException("IEntityManager not registered");
        client  = factory.CreateClient();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        client.Dispose();
        factory.Dispose();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    async Task<long> InsertUserAsync(string name, string? permissionsJson = null)
    {
        return await db.Insert<User>()
                       .Columns(u => u.Name, u => u.Email, u => u.Enabled, u => u.Permissions, u => u.CreatedAt)
                       .Values(name, $"{name}@test.com", true, permissionsJson, DateTime.UtcNow)
                       .ReturnID()
                       .ExecuteAsync();
    }

    async Task<HttpResponseMessage> GetUserAsync(long userId)
        => await client.GetAsync($"/api/users/{userId}");

    async Task<HttpResponseMessage> GetUsersAsync()
        => await client.GetAsync("/api/users");

    async Task<HttpResponseMessage> PatchUserAsync(long userId, string patchBody)
    {
        using StringContent content = new(patchBody, Encoding.UTF8, "application/json");
        return await client.PatchAsync($"/api/users/{userId}", content);
    }

    static string[] ReadPermissions(UserDetails user)
        => user.Permissions ?? [];

    // -----------------------------------------------------------------------
    // GET /api/users/{id} — Permissions surfaced as string[]
    // -----------------------------------------------------------------------

    [Test]
    public async Task GetUserById_WithPermissions_ReturnsPermissionsAsArray()
    {
        long id = await InsertUserAsync("get-perm-user-" + Guid.NewGuid().ToString("N")[..6],
                                        Json.WriteString(new[] { "admin", "read" }));

        HttpResponseMessage resp = await GetUserAsync(id);
        Assert.That((int)resp.StatusCode, Is.EqualTo(200));

        string body = await resp.Content.ReadAsStringAsync();
        UserDetails user = Json.Read<UserDetails>(body)!;

        Assert.That(ReadPermissions(user), Is.EquivalentTo(new[] { "admin", "read" }));
    }

    [Test]
    public async Task GetUserById_NullPermissions_ReturnsEmptyArray()
    {
        long id = await InsertUserAsync("get-nullperm-user-" + Guid.NewGuid().ToString("N")[..6],
                                        permissionsJson: null);

        HttpResponseMessage resp = await GetUserAsync(id);
        Assert.That((int)resp.StatusCode, Is.EqualTo(200));

        string body = await resp.Content.ReadAsStringAsync();
        UserDetails user = Json.Read<UserDetails>(body)!;

        Assert.That(ReadPermissions(user), Is.Empty);
    }

    // -----------------------------------------------------------------------
    // GET /api/users — list includes Permissions per user
    // -----------------------------------------------------------------------

    [Test]
    public async Task ListUsers_PermissionsFieldPresent_ReturnsArrayPerUser()
    {
        long id = await InsertUserAsync("list-perm-user-" + Guid.NewGuid().ToString("N")[..6],
                                        Json.WriteString(new[] { "write" }));

        HttpResponseMessage resp = await GetUsersAsync();
        Assert.That((int)resp.StatusCode, Is.EqualTo(200));

        string body = await resp.Content.ReadAsStringAsync();
        Page<UserDetails> page = Json.Read<Page<UserDetails>>(body)!;
        UserDetails? found = page.Result?.FirstOrDefault(u => u.Id == id);

        Assert.That(found, Is.Not.Null, $"user id={id} was not found in the list response");
        Assert.That(ReadPermissions(found!), Is.EquivalentTo(new[] { "write" }));
    }

    // -----------------------------------------------------------------------
    // PATCH — natural array shape
    // -----------------------------------------------------------------------

    [Test]
    public async Task PatchUser_ArrayShape_UpdatesPermissions()
    {
        long id = await InsertUserAsync("patch-arr-user-" + Guid.NewGuid().ToString("N")[..6],
                                        Json.WriteString(new[] { "read" }));

        string patchBody = "[{\"op\":\"replace\",\"path\":\"/Permissions\",\"value\":[\"admin\",\"write\"]}]";
        HttpResponseMessage patchResp = await PatchUserAsync(id, patchBody);
        Assert.That((int)patchResp.StatusCode, Is.EqualTo(200),
            $"PATCH with natural array shape must return 200; body: {await patchResp.Content.ReadAsStringAsync()}");

        HttpResponseMessage getResp = await GetUserAsync(id);
        string body = await getResp.Content.ReadAsStringAsync();
        UserDetails user = Json.Read<UserDetails>(body)!;

        Assert.That(ReadPermissions(user), Is.EquivalentTo(new[] { "admin", "write" }));
    }

    // -----------------------------------------------------------------------
    // PATCH — string value (not an array) → 400
    // -----------------------------------------------------------------------

    [Test]
    public async Task PatchUser_StringValue_Returns400()
    {
        long id = await InsertUserAsync("patch-legacy-user-" + Guid.NewGuid().ToString("N")[..6],
                                        Json.WriteString(new[] { "read" }));

        // Callers must send an array; a bare string (even if JSON-encoded) is rejected.
        string patchBody = "[{\"op\":\"replace\",\"path\":\"/Permissions\",\"value\":\"[\\\"admin\\\"]\"}]";
        HttpResponseMessage patchResp = await PatchUserAsync(id, patchBody);
        Assert.That((int)patchResp.StatusCode, Is.EqualTo(400),
            $"PATCH with a string Permissions value must return 400; body: {await patchResp.Content.ReadAsStringAsync()}");
    }

    // -----------------------------------------------------------------------
    // PATCH — invalid value type (number) → 400
    // -----------------------------------------------------------------------

    [Test]
    public async Task PatchUser_InvalidPermissionsValue_Number_Returns400()
    {
        long id = await InsertUserAsync("patch-invalid-user-" + Guid.NewGuid().ToString("N")[..6]);

        string patchBody = "[{\"op\":\"replace\",\"path\":\"/Permissions\",\"value\":42}]";
        HttpResponseMessage resp = await PatchUserAsync(id, patchBody);
        Assert.That((int)resp.StatusCode, Is.EqualTo(400),
            "PATCH with a numeric Permissions value must return 400");
    }
}
