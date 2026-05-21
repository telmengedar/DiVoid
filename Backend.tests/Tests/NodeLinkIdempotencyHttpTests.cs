using System.Net;
using System.Net.Http;
using System.Text;
using Backend.Models.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Pooshit.Http;

namespace Backend.tests.Tests;

/// <summary>
/// Load-bearing regression tests for bug #702 — duplicate POST and non-existent DELETE
/// on the link endpoints must be idempotent (HTTP 2xx), not HTTP 500.
///
/// ASP.NET Core maps void-Task controller actions to HTTP 200 OK; both POST and DELETE
/// return 200 on success regardless of whether the link was actually inserted/removed.
///
/// POSITIVE PROOF: with the idempotent fix in NodeService.LinkNodes / UnlinkNodes these
/// tests pass — duplicate POST returns 200, non-existent DELETE returns 200.
///
/// NEGATIVE PROOF: revert NodeService.LinkNodes to throw InvalidOperationException on
/// duplicate — the second POST assertion fails with HTTP 500.
/// Revert NodeService.UnlinkNodes to throw NotFoundException on missing link — the
/// non-existent DELETE assertion fails with HTTP 500.
/// </summary>
[TestFixture]
public class NodeLinkIdempotencyHttpTests
{
    WebApplicationFactory<Program> factory = null!;
    HttpClient client = null!;
    IHttpService http = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        factory = TestSetup.CreateTestFactory();
        client = factory.CreateClient();
        http = TestSetup.HttpServiceFor(factory);
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

    async Task<long> CreateNodeAsync(string name)
    {
        NodeDetails created = await http.Post<NodeDetails, NodeDetails>(
            $"{TestSetup.BaseUrl}/api/nodes",
            new NodeDetails { Type = "task", Name = name },
            new HttpOptions());
        return created.Id;
    }

    Task<HttpResponseMessage> PostLinkAsync(long sourceId, long targetId)
    {
        StringContent body = new($"{targetId}", Encoding.UTF8, "application/json");
        return client.PostAsync($"/api/nodes/{sourceId}/links", body);
    }

    Task<HttpResponseMessage> DeleteLinkAsync(long sourceId, long targetId)
        => client.DeleteAsync($"/api/nodes/{sourceId}/links/{targetId}");

    // -----------------------------------------------------------------------
    // POST /api/nodes/{id}/links — new link → success
    // -----------------------------------------------------------------------

    [Test]
    public async Task PostLink_NewLink_Returns2xx()
    {
        long a = await CreateNodeAsync("LinkIdempotency_A1");
        long b = await CreateNodeAsync("LinkIdempotency_B1");

        HttpResponseMessage resp = await PostLinkAsync(a, b);

        Assert.That((int) resp.StatusCode, Is.InRange(200, 299),
            "first POST of a new link must return a 2xx success status");
    }

    // -----------------------------------------------------------------------
    // POST /api/nodes/{id}/links — duplicate link → idempotent 2xx (bug #702)
    // -----------------------------------------------------------------------

    [Test]
    public async Task PostLink_DuplicateLink_Returns2xx()
    {
        long a = await CreateNodeAsync("LinkIdempotency_A2");
        long b = await CreateNodeAsync("LinkIdempotency_B2");
        await PostLinkAsync(a, b);

        // Second POST of the same link must be a no-op, not HTTP 500 (bug #702)
        HttpResponseMessage resp = await PostLinkAsync(a, b);

        Assert.That((int) resp.StatusCode, Is.InRange(200, 299),
            "duplicate POST must return a 2xx status, not 500 (bug #702 regression)");
    }

    [Test]
    public async Task PostLink_DuplicateLinkReverseDirection_Returns2xx()
    {
        long a = await CreateNodeAsync("LinkIdempotency_A3");
        long b = await CreateNodeAsync("LinkIdempotency_B3");
        await PostLinkAsync(a, b);

        // Reverse-direction POST on an undirected graph is also a duplicate
        HttpResponseMessage resp = await PostLinkAsync(b, a);

        Assert.That((int) resp.StatusCode, Is.InRange(200, 299),
            "reverse-direction duplicate POST must return a 2xx status (undirected graph — bug #702 regression)");
    }

    // -----------------------------------------------------------------------
    // DELETE /api/nodes/{a}/links/{b} — non-existent link → idempotent 2xx
    // -----------------------------------------------------------------------

    [Test]
    public async Task DeleteLink_NonExistentLink_Returns2xx()
    {
        long a = await CreateNodeAsync("LinkIdempotency_A4");
        long b = await CreateNodeAsync("LinkIdempotency_B4");

        // No link was ever created between a and b
        HttpResponseMessage resp = await DeleteLinkAsync(a, b);

        Assert.That((int) resp.StatusCode, Is.InRange(200, 299),
            "DELETE of a non-existent link must return a 2xx status (idempotent — bug #702 sibling check)");
    }

    [Test]
    public async Task DeleteLink_ExistingLink_Returns2xx()
    {
        long a = await CreateNodeAsync("LinkIdempotency_A5");
        long b = await CreateNodeAsync("LinkIdempotency_B5");
        await PostLinkAsync(a, b);

        HttpResponseMessage resp = await DeleteLinkAsync(a, b);

        Assert.That((int) resp.StatusCode, Is.InRange(200, 299),
            "DELETE of an existing link must return a 2xx success status");
    }
}
