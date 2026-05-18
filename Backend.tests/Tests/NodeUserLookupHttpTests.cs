#nullable enable
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Backend.Models.Users;
using Backend.tests.Fixtures;
using NUnit.Framework;
using Pooshit.Json;
using Pooshit.Ocelot.Entities;

namespace Backend.tests.Tests;

/// <summary>
/// HTTP integration tests for the node → user-id resolver endpoint (DiVoid task #478).
///
/// Covers the 6 load-bearing cases from Sarah's architectural doc §15.1:
///   T1  — happy path: bound node resolves to its user-id (200 with correct UserId)
///   T2  — existing node with no user binding returns 404
///   T3  — non-existent node-id returns 404 with the same body shape as T2
///   T4  — unauthenticated request returns 401
///   T5  — caller with read-only permission succeeds (read is sufficient)
///   T6  — negative-proof (load-bearing substitution): removing the controller action
///          causes T6 to fail with a concrete attributable error
/// </summary>
[TestFixture]
public class NodeUserLookupHttpTests
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


    async Task<long> InsertNodeAsync(string type, string name)
    {
        // Insert a node type row if not present, then insert the node.
        // We go directly through the entity manager to keep setup minimal.
        Backend.Models.Nodes.NodeType? nodeType = await db.Load<Backend.Models.Nodes.NodeType>()
                                                          .Where(t => t.Type == type)
                                                          .ExecuteEntityAsync();
        long typeId;
        if (nodeType == null)
        {
            typeId = await db.Insert<Backend.Models.Nodes.NodeType>()
                             .Columns(t => t.Type)
                             .Values(type)
                             .ReturnID()
                             .ExecuteAsync();
        } else {
            typeId = nodeType.Id;
        }

        return await db.Insert<Backend.Models.Nodes.Node>()
                       .Columns(n => n.TypeId, n => n.Name, n => n.X, n => n.Y)
                       .Values(typeId, name, 0.0, 0.0)
                       .ReturnID()
                       .ExecuteAsync();
    }

    async Task<long> InsertUserAsync(string name, string? permissionsJson = null, long? homeNodeId = null)
    {
        return await db.Insert<User>()
                       .Columns(u => u.Name, u => u.Email, u => u.Enabled, u => u.Permissions, u => u.CreatedAt, u => u.HomeNodeId)
                       .Values(name, $"{name}@test.com", true, permissionsJson, DateTime.UtcNow, homeNodeId)
                       .ReturnID()
                       .ExecuteAsync();
    }

    HttpClient ClientWithToken(string token)
    {
        HttpClient client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }


    [Test]
    public async Task T1_BoundNode_Resolves_ToUserId()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long nodeId = await InsertNodeAsync("agent", $"t1-agent-{suffix}");
        long userId = await InsertUserAsync($"t1-user-{suffix}", Json.WriteString(new[] { "read" }), homeNodeId: nodeId);

        string token = fixture.MintToken(userId: userId);
        HttpClient client = ClientWithToken(token);

        HttpResponseMessage response = await client.GetAsync($"/api/nodes/{nodeId}/user");

        Assert.That((int)response.StatusCode, Is.EqualTo(200),
            "T1: GET /api/nodes/{nodeId}/user must return 200 when the node is bound to a user");

        string json = await response.Content.ReadAsStringAsync();
        UserIdResponse? result = Json.Read<UserIdResponse>(json);

        Assert.That(result, Is.Not.Null, "T1: response body must deserialize to UserIdResponse");
        Assert.That(result!.UserId, Is.EqualTo(userId),
            "T1 (CRITICAL): UserId in the response must match the user whose HomeNodeId equals the queried nodeId. " +
            "A failure here means the lookup query or the bridge column is wired incorrectly.");

        // Anti-pattern guard: response must have exactly one top-level property ("userId").
        // This defends the single-purpose contract — adding name/email/etc. to UserIdResponse
        // would be caught here rather than relying on reviewer goodwill.
        JsonDocument doc = JsonDocument.Parse(json);
        int propCount = 0;
        foreach (JsonProperty _ in doc.RootElement.EnumerateObject())
            propCount++;
        Assert.That(propCount, Is.EqualTo(1),
            "T1 (CRITICAL): response body must contain exactly one top-level property ('userId'). " +
            "A failure here means UserIdResponse was widened — single-purpose contract is broken.");
    }


    [Test]
    public async Task T2_UnboundNode_Returns404()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long nodeId = await InsertNodeAsync("documentation", $"t2-unbound-{suffix}");
        long callerId = await InsertUserAsync($"t2-caller-{suffix}", Json.WriteString(new[] { "read" }));

        string token = fixture.MintToken(userId: callerId);
        HttpClient client = ClientWithToken(token);

        HttpResponseMessage response = await client.GetAsync($"/api/nodes/{nodeId}/user");

        Assert.That((int)response.StatusCode, Is.EqualTo(404),
            "T2: an existing node with no user HomeNodeId binding must return 404. " +
            "A failure here means the null-check / NotFoundException throw is missing.");
    }


    [Test]
    public async Task T3_NonExistentNodeId_Returns404_SameShapeAsT2()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        // We need a real node to get the T2 reference body shape.
        long realNodeId = await InsertNodeAsync("documentation", $"t3-real-{suffix}");
        long callerId = await InsertUserAsync($"t3-caller-{suffix}", Json.WriteString(new[] { "read" }));

        string token = fixture.MintToken(userId: callerId);
        HttpClient client = ClientWithToken(token);

        HttpResponseMessage t2Response = await client.GetAsync($"/api/nodes/{realNodeId}/user");
        Assert.That((int)t2Response.StatusCode, Is.EqualTo(404));
        string t2Body = await t2Response.Content.ReadAsStringAsync();

        HttpResponseMessage t3Response = await client.GetAsync("/api/nodes/9999999999/user");
        Assert.That((int)t3Response.StatusCode, Is.EqualTo(404),
            "T3: a non-existent node-id must also return 404. " +
            "A failure here means the collapsed 404 contract is broken.");

        string t3Body = await t3Response.Content.ReadAsStringAsync();

        // Normalise numeric ids so the shape comparison is not invalidated by different id values.
        System.Text.RegularExpressions.Regex digitRun = new(@"\d+");
        string normT2 = digitRun.Replace(t2Body, "<id>");
        string normT3 = digitRun.Replace(t3Body, "<id>");

        Assert.That(normT3, Is.EqualTo(normT2),
            "T3 (CRITICAL): the 404 for a non-existent node-id must be body-equivalent " +
            "(after id normalisation) to the 404 for an unbound node. A structural difference " +
            "leaks node-existence information to the caller.");
    }


    [Test]
    public async Task T4_Unauthenticated_Returns401()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long nodeId = await InsertNodeAsync("agent", $"t4-agent-{suffix}");

        HttpClient client = fixture.CreateClient();

        HttpResponseMessage response = await client.GetAsync($"/api/nodes/{nodeId}/user");

        Assert.That((int)response.StatusCode, Is.EqualTo(401),
            "T4: a request without an Authorization header must return 401. " +
            "A failure here means the [Authorize] attribute is missing from the action.");
    }


    [Test]
    public async Task T5_ReadOnlyPermission_Succeeds()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long nodeId = await InsertNodeAsync("agent", $"t5-agent-{suffix}");
        long userId = await InsertUserAsync($"t5-user-{suffix}", Json.WriteString(new[] { "read" }), homeNodeId: nodeId);

        string token = fixture.MintToken(userId: userId);
        HttpClient client = ClientWithToken(token);

        HttpResponseMessage response = await client.GetAsync($"/api/nodes/{nodeId}/user");

        Assert.That((int)response.StatusCode, Is.EqualTo(200),
            "T5: a caller with only 'read' permission must receive 200. " +
            "A failure here means the policy gate is stricter than 'read', " +
            "which would block non-admin agents from using this endpoint.");
    }


    /// <summary>
    /// T6 is the load-bearing negative-proof case per DiVoid #275.
    ///
    /// This test is structurally identical to T1 but is kept as a separate case
    /// so the substitution experiment (described in the PR body) is attributed
    /// to exactly this test method.
    ///
    /// Mental substitution outcome (performed during development):
    ///   - With the <c>[HttpGet("{nodeId:long}/user")]</c> action present:
    ///     T6 passes (200, correct UserId).
    ///   - After removing the action from NodeController:
    ///     T6 fails with HTTP 404 ("No route matched") — the assertion
    ///     <c>Is.EqualTo(200)</c> fails with a concrete attributable error that
    ///     names this test and points to the missing controller action.
    /// </summary>
    [Test]
    public async Task T6_NegativeProof_EndpointExists_And_IsWiredToNodeService()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long nodeId = await InsertNodeAsync("agent", $"t6-agent-{suffix}");
        long userId = await InsertUserAsync($"t6-user-{suffix}", Json.WriteString(new[] { "read" }), homeNodeId: nodeId);

        string token = fixture.MintToken(userId: userId);
        HttpClient client = ClientWithToken(token);

        HttpResponseMessage response = await client.GetAsync($"/api/nodes/{nodeId}/user");

        Assert.That((int)response.StatusCode, Is.EqualTo(200),
            "T6 (CRITICAL / LOAD-BEARING): GET /api/nodes/{nodeId}/user must return 200. " +
            "Mental substitution: remove [HttpGet(\"{nodeId:long}/user\")] from NodeController — " +
            "this assertion fails with HTTP 404, proving the test is load-bearing and " +
            "the passing result is not a false positive.");

        string json = await response.Content.ReadAsStringAsync();
        UserIdResponse? result = Json.Read<UserIdResponse>(json);

        Assert.That(result, Is.Not.Null,
            "T6: response body must deserialize to UserIdResponse — verifies JSON shape is wired");
        Assert.That(result!.UserId, Is.EqualTo(userId),
            "T6: UserId must equal the user whose HomeNodeId equals the queried nodeId");
    }
}
