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
using Backend.Models.Organizations;
using Backend.Models.Users;
using Backend.tests.Fixtures;
using NUnit.Framework;
using Pooshit.Json;
using Pooshit.Ocelot.Entities;

namespace Backend.tests.Tests;

/// <summary>
/// HTTP integration smoke for the organizations visibility layer (DiVoid #1725).
/// Two users in two different orgs; verifies cross-org list / get is filtered.
/// </summary>
[TestFixture]
public class OrganizationVisibilityHttpTests
{

    JwtAuthFixture fixture = null!;
    IEntityManager db = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        fixture = new JwtAuthFixture();
        db = fixture.EntityManager;
    }

    [OneTimeTearDown]
    public void TearDown() => fixture.Dispose();

    async Task<long> CreateUserAsync(string label, string[] permissions)
        => await db.Insert<User>()
                   .Columns(u => u.Name, u => u.Email, u => u.Enabled, u => u.Permissions, u => u.CreatedAt)
                   .Values($"org-vis-{label}-{Guid.NewGuid():N}", $"orgvis-{label}@test.com", true,
                           Json.WriteString(permissions), DateTime.UtcNow)
                   .ReturnID()
                   .ExecuteAsync();

    async Task<long> CreateOrgAsync(string name)
        => await db.Insert<Organization>()
                   .Columns(o => o.Name, o => o.OwnerId, o => o.Created, o => o.LastUpdate)
                   .Values($"{name}-{Guid.NewGuid():N}", 0L, DateTime.UtcNow, DateTime.UtcNow)
                   .ReturnID()
                   .ExecuteAsync();

    async Task LinkMembershipAsync(long userId, long orgId)
        => await db.Insert<UserOrganization>()
                   .Columns(m => m.UserId, m => m.OrganizationId)
                   .Values(userId, orgId)
                   .ExecuteAsync();

    HttpClient AuthClient(long userId)
    {
        HttpClient client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", fixture.MintToken(userId: userId));
        return client;
    }

    static async Task<NodeDetails> PostNodeAsync(HttpClient client, NodeDetails payload)
    {
        string json = Json.WriteString(payload);
        using StringContent body = new(json, Encoding.UTF8, "application/json");
        HttpResponseMessage resp = await client.PostAsync("/api/nodes", body);
        resp.EnsureSuccessStatusCode();
        return Json.Read<NodeDetails>(await resp.Content.ReadAsStringAsync())!;
    }

    static async Task<List<long>> ListNodeIdsAsync(HttpClient client, string queryName)
    {
        HttpResponseMessage resp = await client.GetAsync($"/api/nodes?name={queryName}");
        resp.EnsureSuccessStatusCode();
        JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        List<long> ids = new();
        if (doc.RootElement.TryGetProperty("result", out JsonElement resultEl))
            foreach (JsonElement el in resultEl.EnumerateArray())
                if (el.TryGetProperty("id", out JsonElement idEl) && idEl.TryGetInt64(out long id))
                    ids.Add(id);
        return ids;
    }

    [Test]
    public async Task CrossOrgUserDoesNotSeeOtherOrgNodeInList()
    {
        long orgA = await CreateOrgAsync("org-a");
        long orgB = await CreateOrgAsync("org-b");
        long userA = await CreateUserAsync("a", ["read", "write"]);
        long userB = await CreateUserAsync("b", ["read", "write"]);
        await LinkMembershipAsync(userA, orgA);
        await LinkMembershipAsync(userB, orgB);

        HttpClient clientA = AuthClient(userA);
        string uniqueName = $"cross-org-visibility-{Guid.NewGuid():N}";
        NodeDetails created = await PostNodeAsync(clientA,
            new NodeDetails { Type = "task", Name = uniqueName, OrganizationId = orgA });

        Assert.That(created.OrganizationId, Is.EqualTo(orgA),
            "newly-created node must be in caller's accessible org");

        HttpClient clientB = AuthClient(userB);
        List<long> visibleToB = await ListNodeIdsAsync(clientB, uniqueName);
        Assert.That(visibleToB, Does.Not.Contain(created.Id),
            "user-B in org-B must not see user-A's org-A node in list");

        List<long> visibleToA = await ListNodeIdsAsync(clientA, uniqueName);
        Assert.That(visibleToA, Does.Contain(created.Id),
            "user-A must see their own org-A node in list");
    }

    [Test]
    public async Task CrossOrgUserGetByIdReturns404()
    {
        long orgA = await CreateOrgAsync("org-a2");
        long orgB = await CreateOrgAsync("org-b2");
        long userA = await CreateUserAsync("a2", ["read", "write"]);
        long userB = await CreateUserAsync("b2", ["read", "write"]);
        await LinkMembershipAsync(userA, orgA);
        await LinkMembershipAsync(userB, orgB);

        HttpClient clientA = AuthClient(userA);
        NodeDetails created = await PostNodeAsync(clientA,
            new NodeDetails { Type = "task", Name = $"cross-org-getbyid-{Guid.NewGuid():N}", OrganizationId = orgA });

        HttpClient clientB = AuthClient(userB);
        HttpResponseMessage resp = await clientB.GetAsync($"/api/nodes/{created.Id}");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound),
            "GET on an invisible-org node must 404 (no existence leak)");
    }

    [Test]
    public async Task AdminSeesAllOrgsRegardlessOfMembership()
    {
        long orgA = await CreateOrgAsync("org-a3");
        long admin = await CreateUserAsync("admin", ["admin", "read", "write"]);
        long member = await CreateUserAsync("member3", ["read", "write"]);
        await LinkMembershipAsync(member, orgA);

        HttpClient memberClient = AuthClient(member);
        NodeDetails created = await PostNodeAsync(memberClient,
            new NodeDetails { Type = "task", Name = $"admin-sees-all-{Guid.NewGuid():N}", OrganizationId = orgA });

        HttpClient adminClient = AuthClient(admin);
        HttpResponseMessage adminResp = await adminClient.GetAsync($"/api/nodes/{created.Id}");
        Assert.That(adminResp.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            "admin must see nodes in orgs they are not a member of");
    }

    async Task<(long otherUser, NodeDetails otherNode)> SeedCrossOrgNodeAsync(string label)
    {
        long orgA = await CreateOrgAsync($"org-a-{label}");
        long orgB = await CreateOrgAsync($"org-b-{label}");
        long userA = await CreateUserAsync($"a-{label}", ["read", "write"]);
        long userB = await CreateUserAsync($"b-{label}", ["read", "write"]);
        await LinkMembershipAsync(userA, orgA);
        await LinkMembershipAsync(userB, orgB);

        HttpClient clientA = AuthClient(userA);
        NodeDetails created = await PostNodeAsync(clientA,
            new NodeDetails { Type = "task", Name = $"cross-org-{label}-{Guid.NewGuid():N}", OrganizationId = orgA });
        return (userB, created);
    }

    [Test]
    public async Task CrossOrgUser_CannotPatchTargetInOtherOrg_Returns404()
    {
        (long otherUser, NodeDetails created) = await SeedCrossOrgNodeAsync("patch");

        HttpClient clientB = AuthClient(otherUser);
        string patch = "[{\"op\":\"replace\",\"path\":\"/name\",\"value\":\"cross-org-hijack\"}]";
        using StringContent body = new(patch, Encoding.UTF8, "application/json");
        HttpResponseMessage resp = await clientB.PatchAsync($"/api/nodes/{created.Id}", body);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound),
            "PATCH on an invisible-org node must 404 (fail-CLOSED on cross-org)");
    }

    [Test]
    public async Task CrossOrgUser_CannotUploadContentToTargetInOtherOrg_Returns404()
    {
        (long otherUser, NodeDetails created) = await SeedCrossOrgNodeAsync("upload");

        HttpClient clientB = AuthClient(otherUser);
        using ByteArrayContent body = new(Encoding.UTF8.GetBytes("attacker-payload"));
        body.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        HttpResponseMessage resp = await clientB.PostAsync($"/api/nodes/{created.Id}/content", body);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound),
            "content upload on an invisible-org node must 404 (fail-CLOSED on cross-org)");
    }

    [Test]
    public async Task CrossOrgUser_CannotReadContentOfTargetInOtherOrg_Returns404()
    {
        (long otherUser, NodeDetails created) = await SeedCrossOrgNodeAsync("getdata");

        HttpClient clientB = AuthClient(otherUser);
        HttpResponseMessage resp = await clientB.GetAsync($"/api/nodes/{created.Id}/content");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound),
            "content GET on an invisible-org node must 404 (fail-CLOSED on cross-org)");
    }

    [Test]
    public async Task CrossOrgUser_CannotCreateLinkToTargetInOtherOrg_Returns404()
    {
        (long otherUser, NodeDetails crossOrgNode) = await SeedCrossOrgNodeAsync("link");
        long otherOrg = await CreateOrgAsync("link-own-org");
        await LinkMembershipAsync(otherUser, otherOrg);
        HttpClient clientB = AuthClient(otherUser);
        NodeDetails own = await PostNodeAsync(clientB,
            new NodeDetails { Type = "task", Name = $"link-own-{Guid.NewGuid():N}", OrganizationId = otherOrg });

        string targetJson = own.Id.ToString();
        using StringContent body = new(targetJson, Encoding.UTF8, "application/json");
        HttpResponseMessage resp = await clientB.PostAsync($"/api/nodes/{crossOrgNode.Id}/links", body);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound),
            "LinkNodes with an invisible-org source must 404 (fail-CLOSED on cross-org)");
    }

    [Test]
    public async Task CrossOrgUser_CannotRemoveLinkInOtherOrg_Returns404()
    {
        (long otherUser, NodeDetails crossOrgNode) = await SeedCrossOrgNodeAsync("unlink");

        HttpClient clientB = AuthClient(otherUser);
        HttpResponseMessage resp = await clientB.DeleteAsync($"/api/nodes/{crossOrgNode.Id}/links/{crossOrgNode.Id + 1}");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound),
            "UnlinkNodes with an invisible-org source must 404 (fail-CLOSED on cross-org)");
    }

    [Test]
    public async Task CrossOrgUser_ListLinksFiltersToVisibleAdjacencyOnly()
    {
        long orgA = await CreateOrgAsync("org-a-links");
        long orgB = await CreateOrgAsync("org-b-links");
        long userA = await CreateUserAsync("a-links", ["read", "write"]);
        long userB = await CreateUserAsync("b-links", ["read", "write"]);
        await LinkMembershipAsync(userA, orgA);
        await LinkMembershipAsync(userB, orgB);

        HttpClient clientA = AuthClient(userA);
        NodeDetails aNode1 = await PostNodeAsync(clientA,
            new NodeDetails { Type = "task", Name = $"links-a1-{Guid.NewGuid():N}", OrganizationId = orgA });
        NodeDetails aNode2 = await PostNodeAsync(clientA,
            new NodeDetails { Type = "task", Name = $"links-a2-{Guid.NewGuid():N}", OrganizationId = orgA });
        HttpResponseMessage linkResp = await clientA.PostAsync($"/api/nodes/{aNode1.Id}/links",
            new StringContent(aNode2.Id.ToString(), Encoding.UTF8, "application/json"));
        linkResp.EnsureSuccessStatusCode();

        HttpClient clientB = AuthClient(userB);
        HttpResponseMessage resp = await clientB.GetAsync($"/api/nodes/links?ids={aNode1.Id},{aNode2.Id}");
        resp.EnsureSuccessStatusCode();
        JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        bool sawAdjacency = false;
        if (doc.RootElement.TryGetProperty("result", out JsonElement resultEl))
            foreach (JsonElement row in resultEl.EnumerateArray())
            {
                long source = row.GetProperty("sourceId").GetInt64();
                long target = row.GetProperty("targetId").GetInt64();
                if ((source == aNode1.Id && target == aNode2.Id) ||
                    (source == aNode2.Id && target == aNode1.Id))
                    sawAdjacency = true;
            }

        Assert.That(sawAdjacency, Is.False,
            "ListLinks must filter supplied ids through caller's visibility before returning adjacency");
    }
}
