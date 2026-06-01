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
    WebApplicationFactory<Program> noAuthFactory = null!;
    IHttpService noAuthHttp = null!;

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

    Task<NodeDetails> NoAuthCreateNodeAsync(NodeDetails payload)
        => noAuthHttp.Post<NodeDetails, NodeDetails>(
            $"{TestSetup.BaseUrl}/api/nodes",
            payload,
            new HttpOptions());

    Task<NodeDetails> NoAuthGetNodeAsync(long id)
        => noAuthHttp.Get<NodeDetails>(
            $"{TestSetup.BaseUrl}/api/nodes/{id}",
            new HttpOptions());

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

    [Test]
    public async Task Create_NoAuth_OwnerIdIs0()
    {
        NodeDetails created = await NoAuthCreateNodeAsync(
            new NodeDetails { Type = "task", Name = "NoAuthOwnerTest" });

        NodeDetails fetched = await NoAuthGetNodeAsync(created.Id);

        Assert.That(fetched.OwnerId, Is.EqualTo(0L),
            "OwnerId must be 0 when auth is disabled (admin-equivalent posture)");
    }

    [Test]
    public async Task List_NoAuth_ReturnsAll()
    {
        NodeDetails n1 = await NoAuthCreateNodeAsync(new NodeDetails { Type = "task", Name = "NoAuthListA" });
        NodeDetails n2 = await NoAuthCreateNodeAsync(new NodeDetails { Type = "task", Name = "NoAuthListB" });

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

    [Test]
    public async Task Create_AuthEnabled_OwnerIdSetFromCallerId()
    {
        long userId = await CreateUserAsync();
        HttpClient client = AuthClient(userId);

        NodeDetails created = await PostNodeAsync(client, new NodeDetails { Type = "task", Name = "OwnershipTest" });

        HttpResponseMessage getResp = await GetNodeRawAsync(client, created.Id);
        getResp.EnsureSuccessStatusCode();
        NodeDetails fetched = Json.Read<NodeDetails>(await getResp.Content.ReadAsStringAsync())!;

        Assert.That(fetched.OwnerId, Is.EqualTo(userId),
            "OwnerId must equal the authenticated caller's DiVoid user-id");
    }

    [Test]
    public async Task Create_BodyOwnerIdIgnored_OverriddenByCallerId()
    {
        long userId = await CreateUserAsync();
        HttpClient client = AuthClient(userId);

        NodeDetails body = new() { Type = "task", Name = "IgnoreBodyOwner", OwnerId = 99999L };
        NodeDetails created = await PostNodeAsync(client, body);

        HttpResponseMessage getResp = await GetNodeRawAsync(client, created.Id);
        NodeDetails fetched = Json.Read<NodeDetails>(await getResp.Content.ReadAsStringAsync())!;

        Assert.That(fetched.OwnerId, Is.EqualTo(userId),
            "body OwnerId must be ignored; server sets OwnerId from the authenticated caller");
    }

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

        NodeDetails publicNode = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = $"PublicVisible-{Guid.NewGuid():N}", Access = NodeAccess.Read });

        List<NodeDetails> results = await GetListResultsAsync(strangerClient);
        long[] ids = results.Select(n => n.Id).ToArray();

        Assert.That(ids, Does.Contain(publicNode.Id),
            "node with Access=Read must appear in the stranger's list result");
    }

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

        NodeDetails node = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = "WriteOpenTest", Access = NodeAccess.Write });

        PatchOperation[] ops = [new() { Op = "replace", Path = "/status", Value = "open" }];
        HttpResponseMessage resp = await PatchNodeRawAsync(strangerClient, node.Id, ops);
        Assert.That((int)resp.StatusCode, Is.EqualTo(200),
            "stranger must be able to patch a node with Access=Write");
    }

    [Test]
    public async Task Patch_AccessAsStranger_OnWritePublicNode_Returns404()
    {
        long ownerId = await CreateUserAsync();
        long strangerId = await CreateUserAsync();
        HttpClient ownerClient = AuthClient(ownerId);
        HttpClient strangerClient = AuthClient(strangerId);

        NodeDetails node = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = "WritePublicAccessGate", Access = NodeAccess.Read | NodeAccess.Write });

        PatchOperation[] ops = [new() { Op = "replace", Path = "/access", Value = 0 }];
        HttpResponseMessage resp = await PatchNodeRawAsync(strangerClient, node.Id, ops);
        Assert.That((int)resp.StatusCode, Is.EqualTo(404),
            "write-public does not grant permission to flip /access — stranger gets 404");
    }

    [Test]
    public async Task Patch_OwnerIdAsStranger_OnWritePublicNode_Returns404()
    {
        long ownerId = await CreateUserAsync();
        long strangerId = await CreateUserAsync();
        HttpClient ownerClient = AuthClient(ownerId);
        HttpClient strangerClient = AuthClient(strangerId);

        NodeDetails node = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = "WritePublicOwnerGate", Access = NodeAccess.Read | NodeAccess.Write });

        PatchOperation[] ops = [new() { Op = "replace", Path = "/ownerId", Value = strangerId }];
        HttpResponseMessage resp = await PatchNodeRawAsync(strangerClient, node.Id, ops);
        Assert.That((int)resp.StatusCode, Is.EqualTo(404),
            "write-public does not grant ownership reassignment — stranger gets 404");
    }

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
    public async Task Patch_OwnerId_Owner_Returns404()
    {
        long ownerId = await CreateUserAsync();
        long strangerId = await CreateUserAsync();
        HttpClient ownerClient = AuthClient(ownerId);

        NodeDetails node = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = "OwnerIdPatchOwner", Access = NodeAccess.None });

        PatchOperation[] ops = [new() { Op = "replace", Path = "/ownerId", Value = strangerId }];
        HttpResponseMessage resp = await PatchNodeRawAsync(ownerClient, node.Id, ops);
        Assert.That((int)resp.StatusCode, Is.EqualTo(404),
            "non-admin attempting to patch /ownerId must receive 404");
    }

    [Test]
    public async Task Patch_Access_Owner_Succeeds()
    {
        long ownerId = await CreateUserAsync();
        HttpClient ownerClient = AuthClient(ownerId);

        NodeDetails node = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = "AccessPatchOwner", Access = NodeAccess.None });

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

        PatchOperation[] ops = [new() { Op = "replace", Path = "/access", Value = 1 }];
        HttpResponseMessage resp = await PatchNodeRawAsync(strangerClient, node.Id, ops);
        Assert.That((int)resp.StatusCode, Is.EqualTo(404),
            "stranger patching /access on a private node must receive 404");
    }

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

    [Test]
    public async Task Backfill_ExistingRow_DefaultsToReadWrite_VisibleToStranger()
    {
        long strangerId = await CreateUserAsync();
        HttpClient strangerClient = AuthClient(strangerId);

        long typeId = await db.Insert<NodeType>()
                              .Columns(t => t.Type)
                              .Values("backfill-test-type")
                              .ReturnID()
                              .ExecuteAsync();

        long nodeId = await db.Insert<Node>()
                              .Columns(n => n.TypeId, n => n.Name)
                              .Values(typeId, $"BackfillDefault-{Guid.NewGuid():N}")
                              .ReturnID()
                              .ExecuteAsync();

        NodeDetails? fetched = null;
        await foreach (Node row in db.Load<Node>(n => n.Id, n => n.OwnerId, n => n.Access)
                                     .Where(n => n.Id == nodeId)
                                     .ExecuteEntitiesAsync())
        {
            fetched = new NodeDetails { Id = row.Id, OwnerId = row.OwnerId, Access = row.Access };
        }

        Assert.That(fetched, Is.Not.Null, "inserted row must be retrievable");
        Assert.That(fetched!.OwnerId, Is.EqualTo(0L), "schema default OwnerId must be 0");
        Assert.That(fetched.Access, Is.EqualTo(NodeAccess.Read | NodeAccess.Write),
            "schema default Access must be Read|Write (3) — preserves pre-access-layer visibility");

        HttpResponseMessage resp = await GetNodeRawAsync(strangerClient, nodeId);
        Assert.That((int)resp.StatusCode, Is.EqualTo(200),
            "backfilled row with default Access=Read|Write must be visible to any authenticated caller");
    }

    [Test]
    public async Task Create_AccessOmitted_DefaultsToReadWrite()
    {
        long userId = await CreateUserAsync();
        HttpClient client = AuthClient(userId);

        string json = Json.WriteString(new NodeDetails { Type = "task", Name = "AccessOmittedDefault" }, JsonOptions.RestApi);
        using StringContent body = new(json, Encoding.UTF8, "application/json");
        HttpResponseMessage postResp = await client.PostAsync("/api/nodes", body);
        postResp.EnsureSuccessStatusCode();
        string postBody = await postResp.Content.ReadAsStringAsync();

        Assert.That(postBody, Does.Contain("\"access\":\"Read, Write\""),
            "POST response wire shape must contain \"access\":\"Read, Write\" when access is omitted from body");

        NodeDetails created = Json.Read<NodeDetails>(postBody)!;
        HttpResponseMessage getResp = await GetNodeRawAsync(client, created.Id);
        getResp.EnsureSuccessStatusCode();
        string getBody = await getResp.Content.ReadAsStringAsync();
        NodeDetails fetched = Json.Read<NodeDetails>(getBody)!;

        Assert.That(fetched.Access, Is.EqualTo(NodeAccess.Read | NodeAccess.Write),
            "GET after omitted-access POST must return Access=Read|Write from database");
    }

    [Test]
    public async Task Create_AccessExplicitlyNone_PreservesNone()
    {
        long userId = await CreateUserAsync();
        HttpClient client = AuthClient(userId);

        string json = Json.WriteString(new NodeDetails { Type = "task", Name = "AccessExplicitNone", Access = NodeAccess.None }, JsonOptions.RestApi);
        using StringContent body = new(json, Encoding.UTF8, "application/json");
        HttpResponseMessage postResp = await client.PostAsync("/api/nodes", body);
        postResp.EnsureSuccessStatusCode();
        string postBody = await postResp.Content.ReadAsStringAsync();

        Assert.That(postBody, Does.Contain("\"access\":\"None\""),
            "POST response wire shape must contain \"access\":\"None\" when access is explicitly set to None");

        NodeDetails created = Json.Read<NodeDetails>(postBody)!;
        HttpResponseMessage getResp = await GetNodeRawAsync(client, created.Id);
        getResp.EnsureSuccessStatusCode();
        NodeDetails fetched = Json.Read<NodeDetails>(await getResp.Content.ReadAsStringAsync())!;

        Assert.That(fetched.Access, Is.EqualTo(NodeAccess.None),
            "GET after explicit-None POST must return Access=None from database");
    }
}
