using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.Http;
using Pooshit.Json;
using Pooshit.Ocelot.Entities;

namespace Backend.tests.Tests;

/// <summary>
/// HTTP-layer integration tests for the opt-in inline <c>linkDetails</c> field on
/// <c>GET /api/nodes</c> (DiVoid task #7156).
///
/// Covers the acceptance matrix:
///   - Default listing has no linkDetails field on any row (regression guard).
///   - <c>?fields=links</c> alone still returns the flat id array unchanged (byte-identical to today).
///   - Happy path: linkDetails carries linkType/context and true source→target orientation.
///   - Empty-row case: node with no incident edges receives linkDetails: [] (empty array, not absent).
///   - Composition: <c>?fields=links,linkDetails</c> returns both fields, independently correct.
///   - sort=linkDetails rejected with 400.
///   - Path-query parity: ?path=...&amp;fields=linkDetails populates linkDetails on terminal-hop rows.
///   - Wire-shape (raw JSON): camelCase keys, linkType serialized as its string enum name.
/// </summary>
[TestFixture]
public class NodeListInlineLinkDetailsHttpTests
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

    async Task<long> CreateNodeAsync(string type = "documentation", string name = "InlineLinkDetailsTest")
    {
        NodeDetails created = await http.Post<NodeDetails, NodeDetails>(
            $"{TestSetup.BaseUrl}/api/nodes",
            new NodeDetails { Type = type, Name = name },
            new HttpOptions());
        return created.Id;
    }

    Task<HttpResponseMessage> LinkAsync(long sourceId, long targetId, string query = "")
        => http.Post<long, HttpResponseMessage>($"{TestSetup.BaseUrl}/api/nodes/{sourceId}/links{query}", targetId);

    Task<HttpResponseMessage> ListRawAsync(string query = "")
        => http.Get<HttpResponseMessage>($"{TestSetup.BaseUrl}/api/nodes{query}");

    static async Task<(List<NodeDetails> Items, string RawJson)> ReadPageWithRawAsync(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync();
        Page<NodeDetails> page = Json.Read<Page<NodeDetails>>(json);
        return (page.Result?.ToList() ?? [], json);
    }

    static async Task<List<NodeDetails>> ReadPageAsync(HttpResponseMessage resp)
    {
        (List<NodeDetails> items, string _) = await ReadPageWithRawAsync(resp);
        return items;
    }

    [Test, Parallelizable]
    [Description("regression guard: plain GET must not carry linkDetails on any row (DiVoid #7156)")]
    public async Task List_DefaultFields_NoLinkDetailsInAnyRow()
    {
        long a = await CreateNodeAsync(name: "DefaultLinkDetailsA");
        long b = await CreateNodeAsync(name: "DefaultLinkDetailsB");
        await LinkAsync(a, b);

        HttpResponseMessage resp = await ListRawAsync($"?id={a},{b}");
        (List<NodeDetails> items, string rawJson) = await ReadPageWithRawAsync(resp);

        NodeDetails nodeA = items.FirstOrDefault(n => n.Id == a)!;
        Assert.That(nodeA, Is.Not.Null, "seeded node must appear in listing");
        Assert.That(nodeA.LinkDetails, Is.Null, "LinkDetails must be null when not in ?fields=");
        Assert.That(rawJson.Contains("\"linkDetails\""), Is.False, "linkDetails key must be absent from JSON in default shape");
    }

    [Test, Parallelizable]
    [Description("existing flat links array must stay byte-identical when linkDetails is not requested (DiVoid #7156 non-breaking guard)")]
    public async Task List_WithLinksFieldOnly_FlatArrayUnchanged()
    {
        long a = await CreateNodeAsync(name: "LinksOnlyUnchangedA");
        long b = await CreateNodeAsync(name: "LinksOnlyUnchangedB");
        await LinkAsync(a, b);

        HttpResponseMessage resp = await ListRawAsync($"?id={a},{b}&fields=id,links");
        (List<NodeDetails> items, string rawJson) = await ReadPageWithRawAsync(resp);

        NodeDetails nodeA = items.FirstOrDefault(n => n.Id == a)!;
        Assert.That(nodeA.Links, Is.Not.Null.And.Contains(b), "links must remain the flat neighbor-id array");
        Assert.That(nodeA.LinkDetails, Is.Null, "linkDetails must be absent when not requested alongside links");
        Assert.That(rawJson.Contains("\"linkDetails\""), Is.False, "linkDetails key must be absent from JSON when only links was requested");
    }

    [Test, Parallelizable]
    [Description("happy path: linkDetails carries linkType/context and true source→target orientation (DiVoid #7156)")]
    public async Task List_WithLinkDetailsField_ReturnsOrientationAndTypeContext()
    {
        long source = await CreateNodeAsync(name: "LinkDetailsOrientationSource");
        long target = await CreateNodeAsync(name: "LinkDetailsOrientationTarget");
        HttpResponseMessage linkResp = await LinkAsync(source, target, "?linkType=Unidirectional&context=subtask");
        Assert.That((int) linkResp.StatusCode, Is.InRange(200, 299));

        HttpResponseMessage resp = await ListRawAsync($"?id={source},{target}&fields=id,linkDetails");
        List<NodeDetails> items = await ReadPageAsync(resp);

        NodeDetails sourceNode = items.FirstOrDefault(n => n.Id == source)!;
        NodeDetails targetNode = items.FirstOrDefault(n => n.Id == target)!;

        Assert.That(sourceNode, Is.Not.Null, "source node must appear");
        Assert.That(sourceNode.LinkDetails, Is.Not.Null, "source node linkDetails must be populated");
        NodeLink sourceEdge = sourceNode.LinkDetails.Single(e => e.TargetId == target || e.SourceId == target);
        Assert.That(sourceEdge.SourceId, Is.EqualTo(source), "orientation must be preserved: source id stays source");
        Assert.That(sourceEdge.TargetId, Is.EqualTo(target), "orientation must be preserved: target id stays target");
        Assert.That(sourceEdge.LinkType, Is.EqualTo(LinkType.Unidirectional));
        Assert.That(sourceEdge.Context, Is.EqualTo("subtask"));

        Assert.That(targetNode, Is.Not.Null, "target node must appear");
        Assert.That(targetNode.LinkDetails, Is.Not.Null, "target node linkDetails must be populated");
        NodeLink targetEdge = targetNode.LinkDetails.Single(e => e.SourceId == source && e.TargetId == target);
        Assert.That(targetEdge.SourceId, Is.EqualTo(source), "target's view of the edge must still report the true source");
        Assert.That(targetEdge.TargetId, Is.EqualTo(target), "target's view of the edge must still report the true target");
        Assert.That(targetEdge.LinkType, Is.EqualTo(LinkType.Unidirectional));
        Assert.That(targetEdge.Context, Is.EqualTo("subtask"));
    }

    [Test, Parallelizable]
    [Description("empty-row case: a node with no incident edges must have linkDetails: [] (empty array, not absent) (DiVoid #7156)")]
    public async Task List_WithLinkDetailsField_IsolatedNode_ReturnsEmptyList()
    {
        long isolated = await CreateNodeAsync(name: "IsolatedLinkDetailsNode");

        HttpResponseMessage resp = await ListRawAsync($"?id={isolated}&fields=id,linkDetails");
        (List<NodeDetails> items, string rawJson) = await ReadPageWithRawAsync(resp);

        NodeDetails node = items.FirstOrDefault(n => n.Id == isolated)!;
        Assert.That(node, Is.Not.Null, "isolated node must appear");
        Assert.That(node.LinkDetails, Is.Not.Null, "linkDetails must be present (not null) when fields=linkDetails requested");
        Assert.That(node.LinkDetails, Is.Empty, "linkDetails must be empty array for node with no incident edges");
        Assert.That(rawJson.Contains("\"linkDetails\""), Is.True, "linkDetails key must appear in JSON even when empty");
    }

    [Test, Parallelizable]
    [Description("composition: ?fields=links,linkDetails returns both fields, independently correct (DiVoid #7156)")]
    public async Task List_WithLinksAndLinkDetailsFields_BothPopulatedIndependently()
    {
        long a = await CreateNodeAsync(name: "ComposedLinksA");
        long b = await CreateNodeAsync(name: "ComposedLinksB");
        await LinkAsync(a, b, "?linkType=Bidirectional&context=references");

        HttpResponseMessage resp = await ListRawAsync($"?id={a},{b}&fields=id,links,linkDetails");
        List<NodeDetails> items = await ReadPageAsync(resp);

        NodeDetails nodeA = items.FirstOrDefault(n => n.Id == a)!;
        Assert.That(nodeA.Links, Is.Not.Null.And.Contains(b), "links must still carry the flat neighbor id");
        Assert.That(nodeA.LinkDetails, Is.Not.Null, "linkDetails must also be populated in the same response");
        NodeLink edge = nodeA.LinkDetails.Single();
        Assert.That(edge.SourceId, Is.EqualTo(a));
        Assert.That(edge.TargetId, Is.EqualTo(b));
        Assert.That(edge.LinkType, Is.EqualTo(LinkType.Bidirectional));
        Assert.That(edge.Context, Is.EqualTo("references"));
    }

    [Test, Parallelizable]
    [Description("zero-row page: ?fields=linkDetails against a non-matching filter must return an empty page without error (DiVoid #7156)")]
    public async Task List_WithLinkDetailsField_NoMatchingRows_ReturnsEmptyPage()
    {
        long nonExistentId = long.MaxValue - 1;

        HttpResponseMessage resp = await ListRawAsync($"?id={nonExistentId}&fields=id,linkDetails");
        List<NodeDetails> items = await ReadPageAsync(resp);

        Assert.That(items, Is.Empty, "a filter matching no rows must return an empty page, not an error");
    }

    [Test, Parallelizable]
    [Description("load-bearing: sort=linkDetails must be rejected with HTTP 400 (DiVoid #7156)")]
    public async Task List_SortByLinkDetails_Returns400()
    {
        HttpResponseMessage resp = await ListRawAsync("?sort=linkDetails&count=1");
        Assert.That((int) resp.StatusCode, Is.EqualTo(400), "sort=linkDetails must be rejected with HTTP 400");
    }

    [Test, Parallelizable]
    [Description("load-bearing: sort=linkDetails must also be rejected with HTTP 400 on the path-query mode (DiVoid #7156)")]
    public async Task ListByPath_SortByLinkDetails_Returns400()
    {
        long projId = await CreateNodeAsync("project", "PathSortLinkDetailsProject");

        HttpResponseMessage resp = await ListRawAsync($"?path=[id:{projId}]&sort=linkDetails&count=1");
        Assert.That((int) resp.StatusCode, Is.EqualTo(400), "sort=linkDetails must be rejected with HTTP 400 in path-query mode too");
    }

    [Test, Parallelizable]
    [Description("path-query parity: ?path=...&fields=linkDetails must return inline edges on terminal-hop rows (DiVoid #7156)")]
    public async Task ListByPath_WithLinkDetailsField_TerminalHopHasInlineEdges()
    {
        long projId = await CreateNodeAsync("project", "PathLinkDetailsProject");
        long docId = await CreateNodeAsync("documentation", "PathLinkDetailsDoc");
        long extraId = await CreateNodeAsync("documentation", "PathLinkDetailsExtra");

        await LinkAsync(projId, docId);
        await LinkAsync(docId, extraId, "?linkType=Unidirectional&context=references");

        HttpResponseMessage resp = await ListRawAsync(
            $"?path=[id:{projId}]/[type:documentation]&fields=id,linkDetails");
        List<NodeDetails> items = await ReadPageAsync(resp);

        NodeDetails doc = items.FirstOrDefault(n => n.Id == docId)!;
        Assert.That(doc, Is.Not.Null, "documentation node must appear in path query result");
        Assert.That(doc.LinkDetails, Is.Not.Null, "path-query terminal hop must have linkDetails populated");
        Assert.That(doc.LinkDetails.Any(e => e.SourceId == docId && e.TargetId == extraId && e.Context == "references"),
            Is.True, "doc must carry the doc→extra edge with its context");
    }

    [Test, Parallelizable]
    [Description("wire-shape: raw JSON uses camelCase keys and serializes linkType as its string enum name (DiVoid #7156 §6.6)")]
    public async Task List_WithLinkDetailsField_RawJsonWireShapeIsCamelCaseWithStringEnum()
    {
        long source = await CreateNodeAsync(name: "WireShapeSource");
        long target = await CreateNodeAsync(name: "WireShapeTarget");
        await LinkAsync(source, target, "?linkType=Bidirectional&context=wireshape");

        HttpResponseMessage resp = await ListRawAsync($"?id={source}&fields=id,linkDetails");
        (List<NodeDetails> _, string rawJson) = await ReadPageWithRawAsync(resp);

        Assert.That(rawJson.Contains("\"linkDetails\":["), Is.True, "linkDetails must serialize as a raw JSON array");
        Assert.That(rawJson.Contains("\"sourceId\":" + source), Is.True, "sourceId must be a camelCase numeric field");
        Assert.That(rawJson.Contains("\"targetId\":" + target), Is.True, "targetId must be a camelCase numeric field");
        Assert.That(rawJson.Contains("\"linkType\":\"Bidirectional\""), Is.True, "linkType must serialize as its string enum name, not a numeric value");
        Assert.That(rawJson.Contains("\"context\":\"wireshape\""), Is.True, "context must serialize as a plain string");
    }

    static async Task<List<NodeDetails>> CollectPage(AsyncPageResponseWriter<NodeDetails> writer)
    {
        byte[] buffer;
        using (MemoryStream ms = new())
        {
            await writer.Write(ms);
            buffer = ms.ToArray();
        }
        using MemoryStream readStream = new(buffer);
        string json = await new StreamReader(readStream).ReadToEndAsync();
        Page<NodeDetails> page = Json.Read<Page<NodeDetails>>(json);
        return page.Result?.ToList() ?? [];
    }

    [Test]
    [Description("defensive: a self-loop row (source==target), unreachable via the API guard in LinkNodes, must be excluded from linkDetails (DiVoid #7156)")]
    public async Task ListPaged_WithLinkDetailsField_SelfLoopRow_IsExcluded()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = new(fixture.EntityManager, new EmbeddingCapability(false));

        NodeDetails self = await svc.CreateNode(new NodeDetails { Type = "task", Name = "SelfLoopLinkDetails" }, callerId: 0);

        await fixture.EntityManager.Insert<NodeLink>()
                     .Columns(l => l.SourceId, l => l.TargetId, l => l.LinkType, l => l.Context)
                     .Values(self.Id, self.Id, LinkType.None, string.Empty)
                     .ExecuteAsync();

        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPaged(
            new NodeFilter { Id = [self.Id], Fields = ["id", "linkDetails"], Count = 10 },
            callerId: 0, isAdmin: true, CancellationToken.None);
        List<NodeDetails> items = await CollectPage(writer);

        NodeDetails node = items.Single(n => n.Id == self.Id);
        Assert.That(node.LinkDetails, Is.Not.Null.And.Empty,
            "a self-loop row must not appear in its own linkDetails — it carries no adjacency information");
    }
}
