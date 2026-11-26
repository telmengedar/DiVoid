using System.Net;
using Backend.Models.Auth;
using Backend.Models.Nodes;
using Backend.Models.Organizations;
using Backend.Models.Users;
using Backend.tests.Fixtures;
using Pooshit.Http;
using Pooshit.Json;
using Pooshit.Ocelot.Tokens;
using static Backend.tests.Fixtures.OrganizationTestEnvironment;

namespace Backend.tests.Tests;

/// <summary>Checks organization assignment and effective HTTP capabilities.</summary>
[TestFixture, Parallelizable]
public class OrganizationAdministrationHttpTests {
    /// <summary>Checks administrative assignment, inheritance and self-profile rejection.</summary>
    /// <param name="jwt">Whether a JWT authenticates the requests.</param>
    /// <returns>The asynchronous operation.</returns>
    [TestCase(true), TestCase(false), Parallelizable]
    public async Task Assignments_CurrentMembershipAndMixedPatches_RespectAdminBoundary(bool jwt) {
        using OrganizationTestEnvironment environment = await OrganizationTestEnvironment.Create();
        long user = await environment.User(environment.OrganizationId);
        long administrator = await environment.User(environment.OtherOrganizationId, "[\"admin\"]");
        HttpOptions ordinary = await environment.Options(user, jwt);
        HttpOptions admin = await environment.Options(administrator, jwt, ["admin"]);
        using HttpResponseMessage created = await environment.Post(admin, new UserParameters { Name = "new-user" }, "users");
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        UserDetails newUser = Json.Read<UserDetails>(await created.Content.ReadAsStringAsync());
        Assert.That(newUser.OrganizationId, Is.Null);
        using HttpResponseMessage nodeCreated = await environment.Post(ordinary, new NodeDetails { Name = "inherited" }, "nodes");
        Assert.That(nodeCreated.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        NodeDetails node = Json.Read<NodeDetails>(await nodeCreated.Content.ReadAsStringAsync());
        Assert.That(node.OrganizationId, Is.EqualTo(environment.OrganizationId));
        foreach (string field in new[] { "/organizationId", "/OrGaNiZaTiOnId", "/permissions", "/ENABLED" }) {
            object value = field.Equals("/permissions", StringComparison.OrdinalIgnoreCase) ? new[] { "admin" } : field.Equals("/enabled", StringComparison.OrdinalIgnoreCase) ? false : environment.OtherOrganizationId;
            using HttpResponseMessage denied = await environment.PatchRequest(ordinary, [Patch("/email", "forged@example.invalid"), Patch(field, value)], "users", "me");
            Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            UserDetails unchanged = await environment.Graph.Users.GetUserById(user);
            Assert.That(unchanged.OrganizationId, Is.EqualTo(environment.OrganizationId));
            Assert.That(unchanged.Email, Is.Not.EqualTo("forged@example.invalid"));
            Assert.That(unchanged.Enabled, Is.True);
            Assert.That(unchanged.Permissions, Does.Not.Contain("admin"));
        }
        using HttpResponseMessage nodeDenied = await environment.PatchRequest(ordinary, [Patch("/access", "Read"), Patch("/organizationId", environment.OtherOrganizationId), Patch("/name", "forged")], "nodes", node.Id.ToString());
        Assert.That(nodeDenied.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        NodeDetails unchangedNode = await environment.Graph.Nodes.GetNodeById(node.Id, user, false);
        Assert.That(unchangedNode.Name, Is.EqualTo("inherited"));
        Assert.That(unchangedNode.Access, Is.EqualTo(NodeAccess.Read | NodeAccess.Write));
        using HttpResponseMessage explicitNull = await environment.PostJson(ordinary, "{\"name\":\"forged-null\",\"organizationId\":null}", "nodes");
        Assert.That(explicitNull.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        using HttpResponseMessage explicitOther = await environment.Post(ordinary, new NodeDetails { Name = "forged-other", OrganizationId = environment.OtherOrganizationId }, "nodes");
        Assert.That(explicitOther.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        using HttpResponseMessage moved = await environment.PatchRequest(admin, [Patch("/organizationId", environment.OtherOrganizationId)], "users", user.ToString());
        Assert.That(moved.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using HttpResponseMessage next = await environment.Post(ordinary, new NodeDetails { Name = "fresh-membership" }, "nodes");
        Assert.That(next.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(Json.Read<NodeDetails>(await next.Content.ReadAsStringAsync()).OrganizationId, Is.EqualTo(environment.OtherOrganizationId));
        using HttpResponseMessage cleared = await environment.PatchRequest(admin, [Patch("/organizationId", null)], "users", user.ToString());
        Assert.That(cleared.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using HttpResponseMessage me = await environment.Get(ordinary, "users", "me");
        Assert.That(Json.Read<UserDetails>(await me.Content.ReadAsStringAsync()).OrganizationId, Is.Null);
        using HttpResponseMessage unassignedCreated = await environment.Post(ordinary, new NodeDetails { Name = "no-organization" }, "nodes");
        Assert.That(unassignedCreated.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(Json.Read<NodeDetails>(await unassignedCreated.Content.ReadAsStringAsync()).OrganizationId, Is.Null);
        using HttpResponseMessage invalid = await environment.PatchRequest(admin, [Patch("/email", "invalid@example.invalid"), Patch("/organizationId", long.MaxValue)], "users", user.ToString());
        Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await environment.Graph.Users.GetUserById(user)).Email, Is.Not.EqualTo("invalid@example.invalid"));
    }

    /// <summary>Checks a restricted key cannot borrow its owner's administration privileges.</summary>
    /// <returns>The asynchronous operation.</returns>
    [Test, Parallelizable]
    public async Task ApiKey_AdminAccountWithReadOnlyKey_CannotWriteOrAdminister() {
        using OrganizationTestEnvironment environment = await OrganizationTestEnvironment.Create();
        long administrator = await environment.User(environment.OrganizationId, "[\"admin\"]");
        HttpOptions read = await environment.Options(administrator, false, ["read"]);
        HttpOptions write = await environment.Options(administrator, false, ["write"]);
        NodeDetails owned = await environment.Graph.Nodes.CreateNode(new NodeDetails { Name = "private-owned", Access = NodeAccess.None }, administrator);
        using HttpResponseMessage visible = await environment.Get(read, "nodes", owned.Id.ToString());
        Assert.That(visible.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using HttpResponseMessage denied = await environment.PatchRequest(read, [Patch("/name", "forged")], "nodes", owned.Id.ToString());
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        using HttpResponseMessage orgDenied = await environment.Post(write, new Organization { Name = "forged-org" }, "organizations");
        Assert.That(orgDenied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        using HttpResponseMessage assignmentDenied = await environment.PatchRequest(write, [Patch("/organizationId", environment.OtherOrganizationId)], "nodes", owned.Id.ToString());
        Assert.That(assignmentDenied.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        NodeDetails hidden = await environment.Graph.Nodes.CreateNode(new NodeDetails { Name = "private-other", Access = NodeAccess.None }, environment.Graph.OwnerId);
        using HttpResponseMessage hiddenDenied = await environment.Get(read, "nodes", hidden.Id.ToString());
        Assert.That(hiddenDenied.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await environment.Graph.Nodes.GetNodeById(owned.Id, administrator, false)).Name, Is.EqualTo("private-owned"));
    }

    /// <summary>Checks CRUD, home anchors and deletion restrictions through the admin API.</summary>
    /// <returns>The asynchronous operation.</returns>
    [Test, Parallelizable]
    public async Task Organizations_AdminCrud_RejectsReferencesAndPreservesGraphAnchor() {
        using OrganizationTestEnvironment environment = await OrganizationTestEnvironment.Create();
        long administrator = await environment.User(null, "[\"admin\"]");
        HttpOptions admin = await environment.Options(administrator);
        HttpOptions ordinary = await environment.Options(environment.Graph.OwnerId);
        using HttpResponseMessage deniedList = await environment.Get(ordinary, "organizations");
        Assert.That(deniedList.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        using HttpResponseMessage created = await environment.Post(admin, new Organization { Name = "managed", HomeNodeId = environment.Graph.RootId }, "organizations");
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Organization organization = Json.Read<Organization>(await created.Content.ReadAsStringAsync());
        Assert.That(organization.HomeNodeId, Is.EqualTo(environment.Graph.RootId));
        using HttpResponseMessage changed = await environment.PatchRequest(admin, [Patch("/name", "renamed"), Patch("/homeNodeId", null)], "organizations", organization.Id.ToString());
        Assert.That(changed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(Json.Read<Organization>(await changed.Content.ReadAsStringAsync()).Name, Is.EqualTo("renamed"));
        using HttpResponseMessage invalidHome = await environment.PatchRequest(admin, [Patch("/name", "invalid"), Patch("/homeNodeId", long.MaxValue)], "organizations", organization.Id.ToString());
        Assert.That(invalidHome.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await environment.Organizations.Get(organization.Id)).Name, Is.EqualTo("renamed"));
        await environment.Assign(administrator, organization.Id);
        Assert.ThrowsAsync<ArgumentException>(() => environment.Organizations.Delete(organization.Id));
        await environment.Assign(administrator, null);
        NodeDetails referenced = await environment.Graph.Nodes.CreateNode(new NodeDetails { Name = "retained", Access = NodeAccess.None }, administrator);
        await environment.Graph.Nodes.Patch(referenced.Id, [Patch("/organizationId", organization.Id)], administrator, true, default);
        Assert.ThrowsAsync<ArgumentException>(() => environment.Organizations.Delete(organization.Id));
        await environment.Graph.Nodes.Patch(referenced.Id, [Patch("/organizationId", null)], administrator, true, default);
        using HttpResponseMessage deleted = await environment.Graph.HttpService().Delete<HttpResponseMessage>(Url("organizations", organization.Id.ToString()), admin);
        Assert.That(deleted.IsSuccessStatusCode, Is.True);
        Assert.That(await environment.Database.Load<Organization>(DB.Count()).Where(o => o.Id == organization.Id).ExecuteScalarAsync<long>(), Is.Zero);
        Assert.That((await environment.Graph.Nodes.GetNodeById(environment.Graph.RootId, environment.Graph.OwnerId, false)).Id, Is.EqualTo(environment.Graph.RootId));
    }
    /// <summary>Rejects non-JSON-pointer fields before mixed security mutations.</summary>
    /// <returns>The asynchronous operation.</returns>
    [Test, Parallelizable]
    public async Task Patch_MalformedSecurityPaths_RejectsWholeRequest() {
        using OrganizationTestEnvironment environment = await OrganizationTestEnvironment.Create();
        long user = await environment.User(environment.OrganizationId);
        HttpOptions ordinary = await environment.Options(user);
        NodeDetails node = await environment.Graph.Nodes.CreateNode(new NodeDetails { Name = "unchanged", Access = NodeAccess.Read | NodeAccess.Write }, environment.Graph.OwnerId);
        foreach ((string path, object value) in new (string, object)[] { ("xorganizationId", environment.OtherOrganizationId), ("xpermissions", new[] { "admin" }), ("xenabled", false) }) {
            using HttpResponseMessage response = await environment.PatchRequest(ordinary, [Patch("/email", "malformed@example.invalid"), Patch(path, value)], "users", "me");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            UserDetails unchanged = await environment.Graph.Users.GetUserById(user);
            Assert.That(unchanged.OrganizationId, Is.EqualTo(environment.OrganizationId));
            Assert.That(unchanged.Email, Is.Not.EqualTo("malformed@example.invalid"));
            Assert.That(unchanged.Enabled, Is.True);
            Assert.That(unchanged.Permissions, Does.Not.Contain("admin"));
        }
        foreach ((string path, object value) in new (string, object)[] { ("xorganizationId", environment.OtherOrganizationId), ("xownerId", user), ("xaccess", "None") }) {
            using HttpResponseMessage response = await environment.PatchRequest(ordinary, [Patch("/name", "forged"), Patch(path, value)], "nodes", node.Id.ToString());
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            NodeDetails unchanged = await environment.Graph.Nodes.GetNodeById(node.Id, user, false);
            Assert.That(unchanged.Name, Is.EqualTo("unchanged"));
            Assert.That(unchanged.OrganizationId, Is.EqualTo(environment.OrganizationId));
            Assert.That(unchanged.OwnerId, Is.EqualTo(environment.Graph.OwnerId));
            Assert.That(unchanged.Access, Is.EqualTo(NodeAccess.Read | NodeAccess.Write));
        }
        using HttpResponseMessage ownerMixed = await environment.PatchRequest(await environment.Options(environment.Graph.OwnerId, false),
            [Patch("/access", "None"), Patch("xorganizationId", environment.OtherOrganizationId)], "nodes", node.Id.ToString());
        Assert.That(ownerMixed.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await environment.Graph.Nodes.GetNodeById(node.Id, user, false)).Access, Is.EqualTo(NodeAccess.Read | NodeAccess.Write));
    }
    /// <summary>Checks issuer-signed organization claims cannot replace persisted membership.</summary>
    /// <returns>The asynchronous operation.</returns>
    [Test, Parallelizable]
    public async Task Jwt_ForgedAndStaleOrganizationClaims_NeverGrantMembership() {
        using OrganizationTestEnvironment environment = await OrganizationTestEnvironment.Create();
        long user = await environment.User(environment.OtherOrganizationId);
        NodeDetails node = await environment.Graph.Nodes.CreateNode(new NodeDetails { Name = "org-protected", Access = NodeAccess.OrgRead }, environment.Graph.OwnerId);
        string token = environment.Graph.Host.MintToken(userId: user, additionalClaims: [
            new System.Security.Claims.Claim("organizationId", environment.OrganizationId.ToString()),
            new System.Security.Claims.Claim("divoid.organization_id", environment.OrganizationId.ToString())
        ]);
        HttpOptions forged = new() { TokenProvider = new NodeChangeTokenProvider(token) };
        using HttpResponseMessage denied = await environment.Get(forged, "nodes", node.Id.ToString());
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        await environment.Assign(user, environment.OrganizationId);
        using HttpResponseMessage admitted = await environment.Get(forged, "nodes", node.Id.ToString());
        Assert.That(admitted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await environment.Assign(user, null);
        using HttpResponseMessage stale = await environment.Get(forged, "nodes", node.Id.ToString());
        Assert.That(stale.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
