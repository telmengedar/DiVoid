using System.Net.Http;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Errors;
using Pooshit.Http;
using Pooshit.Json;

namespace Backend.tests.Tests;

/// <summary>
/// HTTP integration tests for GET /api/nodes/{id} — regression coverage for DiVoid bug #701.
///
/// Bug: the endpoint returned 200 + empty body for a non-existent node instead of
/// 404 with the standard {"code":"data_entitynotfound","text":"..."} envelope.
///
/// Covers:
///   - Happy path: existing node returns 200 with the node JSON.
///   - Bug #701: non-existent node-id returns 404 with data_entitynotfound code.
/// </summary>
[TestFixture]
public class NodeGetByIdHttpTests
{
    WebApplicationFactory<Program> factory = null!;
    IHttpService http = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        factory = TestSetup.CreateTestFactory();
        http = TestSetup.HttpServiceFor(factory);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        factory.Dispose();
    }


    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    async Task<NodeDetails> CreateNodeAsync(string type = "task", string name = "GetByIdTestNode")
    {
        return await http.Post<NodeDetails, NodeDetails>(
            $"{TestSetup.BaseUrl}/api/nodes",
            new NodeDetails { Type = type, Name = name },
            new HttpOptions());
    }

    Task<HttpResponseMessage> GetByIdRawAsync(long nodeId)
        => http.Get<HttpResponseMessage>($"{TestSetup.BaseUrl}/api/nodes/{nodeId}");


    // -----------------------------------------------------------------------
    // Happy path — existing node returns 200 with the node JSON (bug #701 regression guard)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that GET /api/nodes/{id} still returns 200 with a populated body
    /// after the bug #701 fix.  A regression here would mean the fix broke the
    /// happy path (e.g., the null-check throws on a valid node).
    ///
    /// POSITIVE PROOF: with the fix present, the endpoint returns 200 and the
    /// deserialized id matches the created node.
    /// NEGATIVE PROOF (mental substitution): if the NotFoundException throw is
    /// moved outside the null guard (i.e., always throws), this assertion fails
    /// with HTTP 404 — proving the null-check is the discriminator, not noise.
    /// </summary>
    [Test]
    public async Task GetById_ExistingNode_Returns200WithNodeJson()
    {
        NodeDetails created = await CreateNodeAsync(name: "GetById_HappyPath");

        HttpResponseMessage resp = await GetByIdRawAsync(created.Id);

        Assert.That((int)resp.StatusCode, Is.EqualTo(200),
            "GET /api/nodes/{id} for an existing node must return 200 (bug #701 happy-path regression guard)");

        string body = await resp.Content.ReadAsStringAsync();
        NodeDetails returned = Json.Read<NodeDetails>(body);

        Assert.That(returned.Id, Is.EqualTo(created.Id),
            "returned node id must match the created node id");
    }


    // -----------------------------------------------------------------------
    // Bug #701 — non-existent node must return 404 with standard error envelope
    // -----------------------------------------------------------------------

    /// <summary>
    /// POSITIVE PROOF: with the fix (null-guard + NotFoundException throw in NodeService.GetNodeById),
    /// this test passes — the middleware maps NotFoundException{Node} to 404.
    /// NEGATIVE PROOF (mental substitution): revert NodeService.GetNodeById to the old form
    /// (return mapper.EntityFromOperation directly without the null check).  The serializer
    /// writes a 200 with an empty body; this assertion fails with status 200, proving
    /// the test is load-bearing and the passing result is not a false positive.
    /// </summary>
    [Test]
    public async Task GetById_NonExistentNode_Returns404(
        [Values(99999999L)] long missingId)
    {
        HttpResponseMessage resp = await GetByIdRawAsync(missingId);

        Assert.That((int)resp.StatusCode, Is.EqualTo(404),
            "GET /api/nodes/{id} for a non-existent node must return 404, not 200 with empty body (bug #701)");
    }

    /// <summary>
    /// Verifies the 404 body carries the standard data_entitynotfound error code,
    /// matching the contract of other 404-returning endpoints (GetContent, GetUser).
    ///
    /// POSITIVE PROOF: with the fix, the middleware serializes NotFoundException{Node}
    /// to {"code":"data_entitynotfound","text":"..."}, so this assertion passes.
    /// NEGATIVE PROOF: revert the fix — the body is empty and Json.Read throws or
    /// returns null, so the Code assertion fails.
    /// </summary>
    [Test]
    public async Task GetById_NonExistentNode_ReturnsEntityNotFoundCode()
    {
        HttpResponseMessage resp = await GetByIdRawAsync(99999998L);
        string body = await resp.Content.ReadAsStringAsync();
        ErrorResponse error = Json.Read<ErrorResponse>(body);

        Assert.That(error.Code, Is.EqualTo("data_entitynotfound"),
            "error code must be data_entitynotfound for missing node (bug #701)");
    }
}
