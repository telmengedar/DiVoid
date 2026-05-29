#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Backend.Models.Users;
using Backend.tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Http;
using Pooshit.Json;
using Pooshit.Ocelot.Entities;

namespace Backend.tests.Tests;

/// <summary>
/// HTTP integration tests for the per-node access layer (DiVoid #1370).
///
/// Two fixture groups:
///   1. Auth-disabled (no-auth factory) — verifies auth-off posture equals admin-equivalent:
///      OwnerId stays 0, all nodes visible.
///   2. Auth-enabled (JwtAuthFixture) — verifies ownership, visibility, and per-property
///      patch gates using minted JWTs.
///
/// Design spec §14 is the reference for the full test matrix.
/// </summary>
[TestFixture]
public class NodeAccessHttpTests
{
    // -----------------------------------------------------------------------
    // Auth-disabled fixture (group 1)
    // -----------------------------------------------------------------------

    WebApplicationFactory<Program> noAuthFactory = null!;
    IHttpService noAuthHttp = null!;

    // -----------------------------------------------------------------------
    // Auth-enabled fixture (group 2)
    // -----------------------------------------------------------------------

    JwtAuthFixture jwtFixture = null!;
    IEntityManager db = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        noAuthFactory = TestSetup.CreateTestFactory();
        noAuthHttp = TestSetup.HttpServiceFor(noAuthFactory);

        jwtFixture = new JwtAuthFixture();
        db = jwtFixture.EntityManager;
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        noAuthFactory.Dispose();
        jwtFixture.Dispose();
    }

    // -----------------------------------------------------------------------
    // Helpers — auth-disabled
    // -----------------------------------------------------------------------

    Task<NodeDetails> NoAuthCreateNodeAsync(NodeDetails payload)
        => noAuthHttp.Post<NodeDetails, NodeDetails>(
            $"{TestSetup.BaseUrl}/api/nodes",
            payload,
            new HttpOptions());

    Task<NodeDetails> NoAuthGetNodeAsync(long id)
        => noAuthHttp.Get<NodeDetails>(
            $"{TestSetup.BaseUrl}/api/nodes/{id}",
            new HttpOptions());

    // -----------------------------------------------------------------------
    // Helpers — auth-enabled
    // -----------------------------------------------------------------------

    async Task<long> CreateUserAsync(string[]? permissions = null)
        => await db.Insert<User>()
                   .Columns(u => u.Name, u => u.Email, u => u.Enabled, u => u.Permissions, u => u.CreatedAt)
                   .Values(
                       $"access-test-{Guid.NewGuid():N}",
                       $"access-test-{Guid.NewGuid():N}@example.com",
                       true,
                       permissions != null ? Json.WriteString(permissions) : Json.WriteString(new[] { "read", "write" }),
                       DateTime.UtcNow)
                   .ReturnID()
                   .ExecuteAsync();

    async Task<long> CreateAdminUserAsync()
        => await db.Insert<User>()
                   .Columns(u => u.Name, u => u.Email, u => u.Enabled, u => u.Permissions, u => u.CreatedAt)
                   .Values(
                       $"admin-{Guid.NewGuid():N}",
                       $"admin-{Guid.NewGuid():N}@example.com",
                       true,
                       Json.WriteString(new[] { "admin", "read", "write" }),
                       DateTime.UtcNow)
                   .ReturnID()
                   .ExecuteAsync();

    HttpClient AuthClient(long userId)
    {
        HttpClient client = jwtFixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", jwtFixture.MintToken(userId: userId));
        return client;
    }

    static async Task<NodeDetails> PostNodeAsync(HttpClient client, NodeDetails payload)
    {
        string json = Json.WriteString(payload, JsonOptions.RestApi);
        using StringContent body = new(json, Encoding.UTF8, "application/json");
        HttpResponseMessage resp = await client.PostAsync("/api/nodes", body);
        resp.EnsureSuccessStatusCode();
        string respBody = await resp.Content.ReadAsStringAsync();
        return Json.Read<NodeDetails>(respBody)!;
    }

    static async Task<HttpResponseMessage> GetNodeRawAsync(HttpClient client, long id)
        => await client.GetAsync($"/api/nodes/{id}");

    static async Task<HttpResponseMessage> PatchNodeRawAsync(HttpClient client, long id, PatchOperation[] ops)
    {
        string json = Json.WriteString(ops, JsonOptions.RestApi);
        using HttpRequestMessage req = new(new HttpMethod("PATCH"), $"/api/nodes/{id}") {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        return await client.SendAsync(req);
    }

    static async Task<HttpResponseMessage> DeleteNodeRawAsync(HttpClient client, long id)
        => await client.DeleteAsync($"/api/nodes/{id}");

    static async Task<List<NodeDetails>> GetListResultsAsync(HttpClient client)
    {
        HttpResponseMessage resp = await client.GetAsync("/api/nodes");
        resp.EnsureSuccessStatusCode();
        string body = await resp.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        List<NodeDetails> list = new();
        if (doc.RootElement.TryGetProperty("result", out JsonElement resultEl))
        {
            foreach (JsonElement el in resultEl.EnumerateArray())
                list.Add(Json.Read<NodeDetails>(el.GetRawText())!);
        }
        return list;
    }

    // -----------------------------------------------------------------------
    // Group 1 — Auth-disabled: admin-equivalent posture
    // -----------------------------------------------------------------------

    [Test]
    public async Task Create_NoAuth_OwnerIdIs0()
    {
        // With auth disabled ResolveCaller() returns (callerId=0, isAdmin=true).
        // CreateNode must use callerId as OwnerId, so OwnerId must be 0.
        NodeDetails created = await NoAuthCreateNodeAsync(
            new NodeDetails { Type = "task", Name = "NoAuthOwnerTest" });

        NodeDetails fetched = await NoAuthGetNodeAsync(created.Id);

        Assert.That(fetched.OwnerId, Is.EqualTo(0L),
            "OwnerId must be 0 when auth is disabled (admin-equivalent posture)");
    }

    [Test]
    public async Task List_NoAuth_ReturnsAll()
    {
        // Auth-disabled: no visibility filter applied — all nodes returned.
        NodeDetails n1 = await NoAuthCreateNodeAsync(new NodeDetails { Type = "task", Name = "NoAuthListA" });
        NodeDetails n2 = await NoAuthCreateNodeAsync(new NodeDetails { Type = "task", Name = "NoAuthListB" });

        // GET list — auth disabled, no filter
        HttpResponseMessage resp = await noAuthFactory.CreateClient().GetAsync("/api/nodes");
        resp.EnsureSuccessStatusCode();
        string body = await resp.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        List<long> ids = new();
        if (doc.RootElement.TryGetProperty("result", out JsonElement resultEl))
        {
            foreach (JsonElement el in resultEl.EnumerateArray())
            {
                if (el.TryGetProperty("id", out JsonElement idEl) && idEl.TryGetInt64(out long id))
                    ids.Add(id);
            }
        }

        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(n1.Id), "N1 must appear in auth-disabled listing");
            Assert.That(ids, Does.Contain(n2.Id), "N2 must appear in auth-disabled listing");
        });
    }

    // -----------------------------------------------------------------------
    // Group 2 — Auth-enabled: OwnerId set from callerId on create
    // -----------------------------------------------------------------------

    [Test]
    public async Task Create_AuthEnabled_OwnerIdSetFromCallerId()
    {
        long userId = await CreateUserAsync();
        HttpClient client = AuthClient(userId);

        NodeDetails created = await PostNodeAsync(client, new NodeDetails { Type = "task", Name = "OwnershipTest" });

        // Re-read to confirm it was persisted
        HttpResponseMessage getResp = await GetNodeRawAsync(client, created.Id);
        getResp.EnsureSuccessStatusCode();
        NodeDetails fetched = Json.Read<NodeDetails>(await getResp.Content.ReadAsStringAsync())!;

        Assert.That(fetched.OwnerId, Is.EqualTo(userId),
            "OwnerId must equal the authenticated caller's DiVoid user-id");
    }

    [Test]
    public async Task Create_BodyOwnerIdIgnored_OverriddenByCallerId()
    {
        // The caller may supply an OwnerId in the body — it must be ignored.
        long userId = await CreateUserAsync();
        HttpClient client = AuthClient(userId);

        // Send body with a different OwnerId
        NodeDetails body = new() { Type = "task", Name = "IgnoreBodyOwner", OwnerId = 99999L };
        NodeDetails created = await PostNodeAsync(client, body);

        HttpResponseMessage getResp = await GetNodeRawAsync(client, created.Id);
        NodeDetails fetched = Json.Read<NodeDetails>(await getResp.Content.ReadAsStringAsync())!;

        Assert.That(fetched.OwnerId, Is.EqualTo(userId),
            "body OwnerId must be ignored; server sets OwnerId from the authenticated caller");
    }

    // -----------------------------------------------------------------------
    // Group 2 — Visibility: owner / admin / stranger / public-read
    // -----------------------------------------------------------------------

    [Test]
    public async Task GetById_Owner_CanReadOwnPrivateNode()
    {
        long ownerId = await CreateUserAsync();
        HttpClient ownerClient = AuthClient(ownerId);

        NodeDetails node = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = "PrivateOwner", Access = NodeAccess.None });

        HttpResponseMessage resp = await GetNodeRawAsync(ownerClient, node.Id);
        Assert.That((int)resp.StatusCode, Is.EqualTo(200),
            "owner must be able to read their own private node");
    }

    [Test]
    public async Task GetById_Stranger_Returns404OnPrivateNode()
    {
        long ownerId = await CreateUserAsync();
        long strangerId = await CreateUserAsync();
        HttpClient ownerClient = AuthClient(ownerId);
        HttpClient strangerClient = AuthClient(strangerId);

        NodeDetails node = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = "PrivateStranger", Access = NodeAccess.None });

        HttpResponseMessage resp = await GetNodeRawAsync(strangerClient, node.Id);
        Assert.That((int)resp.StatusCode, Is.EqualTo(404),
            "stranger must get 404 on a private node (existence-leak avoidance)");
    }

    [Test]
    public async Task GetById_Admin_CanReadAnyNode()
    {
        long ownerId = await CreateUserAsync();
        long adminId = await CreateAdminUserAsync();
        HttpClient ownerClient = AuthClient(ownerId);
        HttpClient adminClient = AuthClient(adminId);

        NodeDetails node = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = "PrivateAdminRead", Access = NodeAccess.None });

        HttpResponseMessage resp = await GetNodeRawAsync(adminClient, node.Id);
        Assert.That((int)resp.StatusCode, Is.EqualTo(200),
            "admin must be able to read any node regardless of Access flags");
    }

    [Test]
    public async Task GetById_PublicReadNode_VisibleToStranger()
    {
        long ownerId = await CreateUserAsync();
        long strangerId = await CreateUserAsync();
        HttpClient ownerClient = AuthClient(ownerId);
        HttpClient strangerClient = AuthClient(strangerId);

        // Create node with Access=Read so any authenticated user can see it
        NodeDetails node = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = "PublicRead", Access = NodeAccess.Read });

        HttpResponseMessage resp = await GetNodeRawAsync(strangerClient, node.Id);
        Assert.That((int)resp.StatusCode, Is.EqualTo(200),
            "node with Access=Read must be visible to any authenticated caller");
    }

    [Test]
    public async Task List_Stranger_PrivateNodeNotVisible()
    {
        long ownerId = await CreateUserAsync();
        long strangerId = await CreateUserAsync();
        HttpClient ownerClient = AuthClient(ownerId);
        HttpClient strangerClient = AuthClient(strangerId);

        // Create private node as owner
        NodeDetails privateNode = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = $"HiddenFromStranger-{Guid.NewGuid():N}", Access = NodeAccess.None });

        List<NodeDetails> results = await GetListResultsAsync(strangerClient);
        long[] ids = results.Select(n => n.Id).ToArray();

        Assert.That(ids, Does.Not.Contain(privateNode.Id),
            "private node must not appear in the stranger's list result");
    }

    [Test]
    public async Task List_PublicNode_VisibleToStranger()
    {
        long ownerId = await CreateUserAsync();
        long strangerId = await CreateUserAsync();
        HttpClient ownerClient = AuthClient(ownerId);
        HttpClient strangerClient = AuthClient(strangerId);

        // Create public node (Access=Read)
        NodeDetails publicNode = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = $"PublicVisible-{Guid.NewGuid():N}", Access = NodeAccess.Read });

        List<NodeDetails> results = await GetListResultsAsync(strangerClient);
        long[] ids = results.Select(n => n.Id).ToArray();

        Assert.That(ids, Does.Contain(publicNode.Id),
            "node with Access=Read must appear in the stranger's list result");
    }

    // -----------------------------------------------------------------------
    // Group 2 — PATCH gates: owner / admin / stranger
    // -----------------------------------------------------------------------

    [Test]
    public async Task Patch_Owner_CanPatchOwnNode()
    {
        long ownerId = await CreateUserAsync();
        HttpClient ownerClient = AuthClient(ownerId);

        NodeDetails node = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = "PatchOwnerTest", Access = NodeAccess.None });

        PatchOperation[] ops = [new() { Op = "replace", Path = "/status", Value = "open" }];
        HttpResponseMessage resp = await PatchNodeRawAsync(ownerClient, node.Id, ops);
        Assert.That((int)resp.StatusCode, Is.EqualTo(200),
            "owner must be able to patch their own node");
    }

    [Test]
    public async Task Patch_Stranger_Returns404OnPrivateNode()
    {
        long ownerId = await CreateUserAsync();
        long strangerId = await CreateUserAsync();
        HttpClient ownerClient = AuthClient(ownerId);
        HttpClient strangerClient = AuthClient(strangerId);

        NodeDetails node = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = "PatchStrangerTest", Access = NodeAccess.None });

        PatchOperation[] ops = [new() { Op = "replace", Path = "/status", Value = "open" }];
        HttpResponseMessage resp = await PatchNodeRawAsync(strangerClient, node.Id, ops);
        Assert.That((int)resp.StatusCode, Is.EqualTo(404),
            "stranger patching a private node must receive 404");
    }

    [Test]
    public async Task Patch_Admin_CanPatchAnyNode()
    {
        long ownerId = await CreateUserAsync();
        long adminId = await CreateAdminUserAsync();
        HttpClient ownerClient = AuthClient(ownerId);
        HttpClient adminClient = AuthClient(adminId);

        NodeDetails node = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = "PatchAdminTest", Access = NodeAccess.None });

        PatchOperation[] ops = [new() { Op = "replace", Path = "/status", Value = "open" }];
        HttpResponseMessage resp = await PatchNodeRawAsync(adminClient, node.Id, ops);
        Assert.That((int)resp.StatusCode, Is.EqualTo(200),
            "admin must be able to patch any node");
    }

    [Test]
    public async Task Patch_WriteOpenNode_Stranger_Succeeds()
    {
        long ownerId = await CreateUserAsync();
        long strangerId = await CreateUserAsync();
        HttpClient ownerClient = AuthClient(ownerId);
        HttpClient strangerClient = AuthClient(strangerId);

        // Node with Write flag open — anyone with write perm can patch it
        NodeDetails node = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = "WriteOpenTest", Access = NodeAccess.Write });

        PatchOperation[] ops = [new() { Op = "replace", Path = "/status", Value = "open" }];
        HttpResponseMessage resp = await PatchNodeRawAsync(strangerClient, node.Id, ops);
        Assert.That((int)resp.StatusCode, Is.EqualTo(200),
            "stranger must be able to patch a node with Access=Write");
    }

    // -----------------------------------------------------------------------
    // Group 2 — PATCH /ownerId gate: admin-only
    // -----------------------------------------------------------------------

    [Test]
    public async Task Patch_OwnerId_Admin_Succeeds()
    {
        long ownerId = await CreateUserAsync();
        long newOwnerId = await CreateUserAsync();
        long adminId = await CreateAdminUserAsync();
        HttpClient ownerClient = AuthClient(ownerId);
        HttpClient adminClient = AuthClient(adminId);

        NodeDetails node = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = "OwnerIdPatchAdmin", Access = NodeAccess.None });

        PatchOperation[] ops = [new() { Op = "replace", Path = "/ownerId", Value = newOwnerId }];
        HttpResponseMessage resp = await PatchNodeRawAsync(adminClient, node.Id, ops);
        Assert.That((int)resp.StatusCode, Is.EqualTo(200),
            "admin must be able to patch /ownerId");
    }

    [Test]
    public async Task Patch_OwnerId_Owner_Returns403()
    {
        long ownerId = await CreateUserAsync();
        long strangerId = await CreateUserAsync();
        HttpClient ownerClient = AuthClient(ownerId);

        NodeDetails node = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = "OwnerIdPatchOwner", Access = NodeAccess.None });

        // Owner tries to change OwnerId — must be refused even though they own the node
        PatchOperation[] ops = [new() { Op = "replace", Path = "/ownerId", Value = strangerId }];
        HttpResponseMessage resp = await PatchNodeRawAsync(ownerClient, node.Id, ops);
        Assert.That((int)resp.StatusCode, Is.EqualTo(403),
            "non-admin must receive 403 when attempting to patch /ownerId");
    }

    // -----------------------------------------------------------------------
    // Group 2 — PATCH /access gate: owner or admin
    // -----------------------------------------------------------------------

    [Test]
    public async Task Patch_Access_Owner_Succeeds()
    {
        long ownerId = await CreateUserAsync();
        HttpClient ownerClient = AuthClient(ownerId);

        NodeDetails node = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = "AccessPatchOwner", Access = NodeAccess.None });

        // Owner changes Access to Read-open
        PatchOperation[] ops = [new() { Op = "replace", Path = "/access", Value = 1 }];
        HttpResponseMessage resp = await PatchNodeRawAsync(ownerClient, node.Id, ops);
        Assert.That((int)resp.StatusCode, Is.EqualTo(200),
            "owner must be able to patch /access");
    }

    [Test]
    public async Task Patch_Access_Admin_Succeeds()
    {
        long ownerId = await CreateUserAsync();
        long adminId = await CreateAdminUserAsync();
        HttpClient ownerClient = AuthClient(ownerId);
        HttpClient adminClient = AuthClient(adminId);

        NodeDetails node = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = "AccessPatchAdmin", Access = NodeAccess.None });

        PatchOperation[] ops = [new() { Op = "replace", Path = "/access", Value = 1 }];
        HttpResponseMessage resp = await PatchNodeRawAsync(adminClient, node.Id, ops);
        Assert.That((int)resp.StatusCode, Is.EqualTo(200),
            "admin must be able to patch /access");
    }

    [Test]
    public async Task Patch_Access_Stranger_Returns404()
    {
        long ownerId = await CreateUserAsync();
        long strangerId = await CreateUserAsync();
        HttpClient ownerClient = AuthClient(ownerId);
        HttpClient strangerClient = AuthClient(strangerId);

        NodeDetails node = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = "AccessPatchStranger", Access = NodeAccess.None });

        // Stranger can't even read the node — 404 before the /access gate fires
        PatchOperation[] ops = [new() { Op = "replace", Path = "/access", Value = 1 }];
        HttpResponseMessage resp = await PatchNodeRawAsync(strangerClient, node.Id, ops);
        Assert.That((int)resp.StatusCode, Is.EqualTo(404),
            "stranger patching /access on a private node must receive 404");
    }

    // -----------------------------------------------------------------------
    // Group 2 — Delete gate
    // -----------------------------------------------------------------------

    [Test]
    public async Task Delete_Owner_Succeeds()
    {
        long ownerId = await CreateUserAsync();
        HttpClient ownerClient = AuthClient(ownerId);

        NodeDetails node = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = "DeleteOwner", Access = NodeAccess.None });

        HttpResponseMessage resp = await DeleteNodeRawAsync(ownerClient, node.Id);
        Assert.That((int)resp.StatusCode, Is.EqualTo(200),
            "owner must be able to delete their own node");
    }

    [Test]
    public async Task Delete_Stranger_Returns404OnPrivateNode()
    {
        long ownerId = await CreateUserAsync();
        long strangerId = await CreateUserAsync();
        HttpClient ownerClient = AuthClient(ownerId);
        HttpClient strangerClient = AuthClient(strangerId);

        NodeDetails node = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = "DeleteStranger", Access = NodeAccess.None });

        HttpResponseMessage resp = await DeleteNodeRawAsync(strangerClient, node.Id);
        Assert.That((int)resp.StatusCode, Is.EqualTo(404),
            "stranger deleting a private node must receive 404");
    }
}
