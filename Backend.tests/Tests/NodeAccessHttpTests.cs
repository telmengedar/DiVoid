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
///
/// Not [Parallelizable]: every test shares the same <c>db</c> and <c>jwtFixture</c>/factory
/// instances seeded once in [OneTimeSetUp], so sibling list-style tests would race over them.
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
    [Description("Regression guard for the access-gate fix: pins both halves together, because either " +
                 "assertion alone passes against a half-fix (status-only misses a still-tampered LastUpdate; " +
                 "timestamp-only misses a still-disclosed node body).")]
    public async Task Patch_Stranger_EmptyPatchOnPrivateNode_Returns404AndLeavesLastUpdateUnchanged()
    {
        long ownerId = await CreateUserAsync();
        long strangerId = await CreateUserAsync();
        HttpClient ownerClient = AuthClient(ownerId);
        HttpClient strangerClient = AuthClient(strangerId);

        NodeDetails node = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = "PatchEmptyStrangerTest", Access = NodeAccess.None });

        HttpResponseMessage beforeResp = await GetNodeRawAsync(ownerClient, node.Id);
        NodeDetails before = Json.Read<NodeDetails>(await beforeResp.Content.ReadAsStringAsync())!;

        HttpResponseMessage resp = await PatchNodeRawAsync(strangerClient, node.Id, []);

        HttpResponseMessage afterResp = await GetNodeRawAsync(ownerClient, node.Id);
        NodeDetails after = Json.Read<NodeDetails>(await afterResp.Content.ReadAsStringAsync())!;

        Assert.Multiple(() => {
            Assert.That((int)resp.StatusCode, Is.EqualTo(404),
                "stranger sending an empty patch array to a private node must receive 404, not the ungated touch-and-disclose path");
            Assert.That(after.LastUpdate, Is.EqualTo(before.LastUpdate),
                "the tail LastUpdate write must not fire when the gate rejects the caller, even on an empty patch array");
        });
    }

    [Test]
    [Description("Regression guard for the access-gate fix: an empty patch from a caller who already " +
                 "passes the gate must stay the pre-fix touch behaviour (200, LastUpdate bumped), not turn " +
                 "into a spurious rejection.")]
    public async Task Patch_Owner_EmptyPatchOnOwnNode_Returns200()
    {
        long ownerId = await CreateUserAsync();
        HttpClient ownerClient = AuthClient(ownerId);

        NodeDetails node = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = "PatchEmptyOwnerTest", Access = NodeAccess.None });

        HttpResponseMessage resp = await PatchNodeRawAsync(ownerClient, node.Id, []);
        Assert.That((int)resp.StatusCode, Is.EqualTo(200),
            "an empty patch array by a permitted caller stays a touch — 200, LastUpdate bumped");
    }

    [Test]
    [Description("Bounds the severity of the access-gate fix's readback: determines whether the " +
                 "isAdmin:true readback Patch relies on could disclose raw content, not just metadata, " +
                 "to a caller who fails the write gate.")]
    public async Task GetById_Owner_WithContent_OmitsContentField()
    {
        long ownerId = await CreateUserAsync();
        HttpClient ownerClient = AuthClient(ownerId);

        NodeDetails node = await PostNodeAsync(ownerClient,
            new NodeDetails { Type = "task", Name = "GetByIdContentReadbackProbe", Access = NodeAccess.None });

        using StringContent uploadBody = new("probe body", Encoding.UTF8, "text/plain");
        HttpResponseMessage uploadResp = await ownerClient.PostAsync($"/api/nodes/{node.Id}/content", uploadBody);
        uploadResp.EnsureSuccessStatusCode();

        HttpResponseMessage getResp = await GetNodeRawAsync(ownerClient, node.Id);
        string rawJson = await getResp.Content.ReadAsStringAsync();

        Assert.That(rawJson.Contains("\"content\""), Is.False,
            "GET /api/nodes/{id} does not encode Content without a ?fields= opt-in");
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

    static async Task LinkAsync(HttpClient client, long sourceId, long targetId)
    {
        using StringContent body = new(targetId.ToString(), Encoding.UTF8, "application/json");
        HttpResponseMessage resp = await client.PostAsync($"/api/nodes/{sourceId}/links", body);
        resp.EnsureSuccessStatusCode();
    }

    static async Task<long> CreateNodeAsync(HttpClient client, string name, NodeAccess access)
        => (await PostNodeAsync(client, new NodeDetails { Type = "task", Name = name, Access = access })).Id;

    static async Task<(long[] Ids, long Total)> PathAsync(HttpClient client, string path)
    {
        HttpResponseMessage resp = await client.GetAsync($"/api/nodes?path={path}");
        resp.EnsureSuccessStatusCode();
        JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        long[] ids = doc.RootElement.GetProperty("result").EnumerateArray()
                        .Select(el => el.GetProperty("id").GetInt64())
                        .ToArray();
        return (ids, doc.RootElement.GetProperty("total").GetInt64());
    }

    static async Task<(long[] Ids, long Total)> ListAsync(HttpClient client, string query)
    {
        HttpResponseMessage resp = await client.GetAsync($"/api/nodes?{query}");
        resp.EnsureSuccessStatusCode();
        JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        long[] ids = doc.RootElement.GetProperty("result").EnumerateArray()
                        .Select(el => el.GetProperty("id").GetInt64())
                        .ToArray();
        return (ids, doc.RootElement.GetProperty("total").GetInt64());
    }

    async Task<(HttpClient Owner, HttpClient Stranger, long A, long P, long B)> BuildPrivateIntermediateGraphAsync(NodeAccess intermediateAccess)
    {
        HttpClient owner = AuthClient(await CreateUserAsync());
        HttpClient stranger = AuthClient(await CreateUserAsync());
        long a = await CreateNodeAsync(owner, $"PathA-{Guid.NewGuid():N}", NodeAccess.Read | NodeAccess.Write);
        long p = await CreateNodeAsync(owner, $"PathP-{Guid.NewGuid():N}", intermediateAccess);
        long b = await CreateNodeAsync(owner, $"PathB-{Guid.NewGuid():N}", NodeAccess.Read | NodeAccess.Write);
        await LinkAsync(owner, a, p);
        await LinkAsync(owner, p, b);
        return (owner, stranger, a, p, b);
    }

    [Test]
    public async Task PathQuery_Stranger_PrivateIntermediate_NotTraversed()
    {
        (HttpClient owner, HttpClient stranger, long a, long p, long b) = await BuildPrivateIntermediateGraphAsync(NodeAccess.None);
        string path = $"[id:{a}]/[]/[]";

        (long[] ownerIds, _) = await PathAsync(owner, path);
        (long[] strangerIds, _) = await PathAsync(stranger, path);

        Assert.Multiple(() => {
            Assert.That(ownerIds, Does.Contain(b), "the route through the private node must exist for its owner");
            Assert.That(strangerIds, Does.Not.Contain(b), "a stranger must not reach B through a private intermediate");
            Assert.That(strangerIds, Does.Not.Contain(p), "the private intermediate must not be listed");
        });
    }

    [Test]
    public async Task PathQuery_Stranger_PrivateSeed_ReturnsEmpty()
    {
        (HttpClient owner, HttpClient stranger, long a, long p, long b) = await BuildPrivateIntermediateGraphAsync(NodeAccess.None);
        string path = $"[id:{p}]/[]";

        (long[] ownerIds, _) = await PathAsync(owner, path);
        (long[] strangerIds, long strangerTotal) = await PathAsync(stranger, path);

        Assert.Multiple(() => {
            Assert.That(ownerIds, Is.EquivalentTo(new[] { a, b }), "the private seed must have neighbours for its owner");
            Assert.That(strangerIds, Is.Empty, "a private seed must yield no rows for a stranger");
            Assert.That(strangerTotal, Is.EqualTo(0L));
        });
    }

    [Test]
    public async Task PathQuery_Stranger_PrivateSecondIntermediate_NotTraversed()
    {
        HttpClient owner = AuthClient(await CreateUserAsync());
        HttpClient stranger = AuthClient(await CreateUserAsync());
        long a = await CreateNodeAsync(owner, $"PathA-{Guid.NewGuid():N}", NodeAccess.Read | NodeAccess.Write);
        long q = await CreateNodeAsync(owner, $"PathQ-{Guid.NewGuid():N}", NodeAccess.Read | NodeAccess.Write);
        long p = await CreateNodeAsync(owner, $"PathP-{Guid.NewGuid():N}", NodeAccess.None);
        long b = await CreateNodeAsync(owner, $"PathB-{Guid.NewGuid():N}", NodeAccess.Read | NodeAccess.Write);
        await LinkAsync(owner, a, q);
        await LinkAsync(owner, q, p);
        await LinkAsync(owner, p, b);
        string path = $"[id:{a}]/[]/[]/[]";

        (long[] ownerIds, _) = await PathAsync(owner, path);
        (long[] strangerIds, _) = await PathAsync(stranger, path);

        Assert.Multiple(() => {
            Assert.That(ownerIds, Does.Contain(b), "the route through the private second intermediate must exist for its owner");
            Assert.That(strangerIds, Does.Not.Contain(b), "a stranger must not reach B through a private second intermediate");
        });
    }

    [Test]
    public async Task PathQuery_Admin_PrivateIntermediate_Traversed()
    {
        (_, _, long a, _, long b) = await BuildPrivateIntermediateGraphAsync(NodeAccess.None);
        HttpClient admin = AuthClient(await CreateAdminUserAsync());

        (long[] ids, _) = await PathAsync(admin, $"[id:{a}]/[]/[]");

        Assert.That(ids, Does.Contain(b), "an admin must traverse private intermediates");
    }

    [Test]
    public async Task PathQuery_Owner_PrivateIntermediate_Traversed()
    {
        (HttpClient owner, _, long a, _, long b) = await BuildPrivateIntermediateGraphAsync(NodeAccess.None);

        (long[] ids, _) = await PathAsync(owner, $"[id:{a}]/[]/[]");

        Assert.That(ids, Does.Contain(b), "the owner of a private intermediate must traverse it");
    }

    [Test]
    public async Task PathQuery_Stranger_ReadFlagIntermediate_Traversed()
    {
        (_, HttpClient stranger, long a, _, long b) = await BuildPrivateIntermediateGraphAsync(NodeAccess.Read);

        (long[] ids, _) = await PathAsync(stranger, $"[id:{a}]/[]/[]");

        Assert.That(ids, Does.Contain(b), "an intermediate with the Read flag must be traversable by a stranger");
    }

    [Test]
    public async Task PathQuery_Stranger_WriteOnlyIntermediate_NotTraversed()
    {
        (HttpClient owner, HttpClient stranger, long a, _, long b) = await BuildPrivateIntermediateGraphAsync(NodeAccess.Write);
        string path = $"[id:{a}]/[]/[]";

        (long[] ownerIds, _) = await PathAsync(owner, path);
        (long[] strangerIds, _) = await PathAsync(stranger, path);

        Assert.Multiple(() => {
            Assert.That(ownerIds, Does.Contain(b), "the route through the write-only node must exist for its owner");
            Assert.That(strangerIds, Does.Not.Contain(b), "traversal needs the Read flag, a Write-only intermediate must not be crossed");
        });
    }

    [Test]
    public async Task LinkedTo_Stranger_PrivateSeed_ReturnsEmpty()
    {
        (HttpClient owner, HttpClient stranger, long a, long p, long b) = await BuildPrivateIntermediateGraphAsync(NodeAccess.None);

        (long[] ownerIds, _) = await ListAsync(owner, $"linkedto={p}");
        (long[] strangerIds, long strangerTotal) = await ListAsync(stranger, $"linkedto={p}");
        (long[] missingIds, long missingTotal) = await ListAsync(stranger, "linkedto=9000000000");

        Assert.Multiple(() => {
            Assert.That(ownerIds, Is.EquivalentTo(new[] { a, b }), "the private seed must have neighbours for its owner");
            Assert.That(strangerIds, Is.Empty, "a private seed must contribute no neighbours for a stranger");
            Assert.That(strangerTotal, Is.EqualTo(0L));
            Assert.That(strangerIds, Is.EqualTo(missingIds), "a private seed must answer like a seed that does not exist");
            Assert.That(strangerTotal, Is.EqualTo(missingTotal));
        });
    }

    [Test]
    public async Task LinkedTo_Stranger_PrivateAndPublicSeeds_OnlyPublicSeedNeighbours()
    {
        (HttpClient owner, HttpClient stranger, long a, long p, long b) = await BuildPrivateIntermediateGraphAsync(NodeAccess.None);
        long c = await CreateNodeAsync(owner, $"LinkedToC-{Guid.NewGuid():N}", NodeAccess.Read | NodeAccess.Write);
        await LinkAsync(owner, a, c);

        (long[] ownerIds, _) = await ListAsync(owner, $"linkedto={p},{a}");
        (long[] strangerIds, _) = await ListAsync(stranger, $"linkedto={p},{a}");

        Assert.Multiple(() => {
            Assert.That(ownerIds, Is.EquivalentTo(new[] { b, c }), "both seeds must contribute for the owner");
            Assert.That(strangerIds, Is.EquivalentTo(new[] { c }), "only the readable seed may contribute for a stranger");
        });
    }

    [Test]
    public async Task LinkedTo_Stranger_PathTerminal_PrivateSeed_Excluded()
    {
        (HttpClient owner, HttpClient stranger, _, long p, long b) = await BuildPrivateIntermediateGraphAsync(NodeAccess.None);
        string query = $"path=[id:{b}]&linkedto={p}";

        (long[] ownerIds, _) = await ListAsync(owner, query);
        (long[] strangerIds, long strangerTotal) = await ListAsync(stranger, query);

        Assert.Multiple(() => {
            Assert.That(ownerIds, Is.EquivalentTo(new[] { b }), "the private seed must reach the path terminal for its owner");
            Assert.That(strangerIds, Is.Empty, "a private seed must not reach the path terminal for a stranger");
            Assert.That(strangerTotal, Is.EqualTo(0L));
        });
    }

    [Test]
    public async Task LinkedTo_Admin_PathTerminal_PrivateSeed_ReturnsTerminal()
    {
        (_, _, _, long p, long b) = await BuildPrivateIntermediateGraphAsync(NodeAccess.None);
        HttpClient admin = AuthClient(await CreateAdminUserAsync());

        (long[] ids, _) = await ListAsync(admin, $"path=[id:{b}]&linkedto={p}");

        Assert.That(ids, Is.EquivalentTo(new[] { b }), "an admin must reach the path terminal through a private seed");
    }

    [Test]
    public async Task LinkedTo_Stranger_WriteOnlySeed_ReturnsEmpty()
    {
        (HttpClient owner, HttpClient stranger, long a, long p, long b) = await BuildPrivateIntermediateGraphAsync(NodeAccess.Write);

        (long[] ownerIds, _) = await ListAsync(owner, $"linkedto={p}");
        (long[] strangerIds, _) = await ListAsync(stranger, $"linkedto={p}");

        Assert.Multiple(() => {
            Assert.That(ownerIds, Is.EquivalentTo(new[] { a, b }), "the write-only seed must have neighbours for its owner");
            Assert.That(strangerIds, Is.Empty, "a Write-only seed must not contribute for a stranger");
        });
    }

    [Test]
    public async Task LinkedTo_Stranger_ReadFlagSeed_ReturnsNeighbours()
    {
        (_, HttpClient stranger, long a, long p, long b) = await BuildPrivateIntermediateGraphAsync(NodeAccess.Read);

        (long[] ids, _) = await ListAsync(stranger, $"linkedto={p}");

        Assert.That(ids, Is.EquivalentTo(new[] { a, b }), "a seed with the Read flag must contribute for a stranger");
    }

    [Test]
    public async Task LinkedTo_Admin_PrivateSeed_ReturnsNeighbours()
    {
        (_, _, long a, long p, long b) = await BuildPrivateIntermediateGraphAsync(NodeAccess.None);
        HttpClient admin = AuthClient(await CreateAdminUserAsync());

        (long[] ids, _) = await ListAsync(admin, $"linkedto={p}");

        Assert.That(ids, Is.EquivalentTo(new[] { a, b }), "an admin must see the neighbours of a private seed");
    }

    [Test]
    public async Task LinkedTo_Stranger_PrivateSeed_UnknownField_Returns400()
    {
        (_, HttpClient stranger, _, long p, _) = await BuildPrivateIntermediateGraphAsync(NodeAccess.None);

        HttpResponseMessage resp = await stranger.GetAsync($"/api/nodes?linkedto={p}&fields=bogus");

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }
}
