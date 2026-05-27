using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.Http;
using Pooshit.Json;

namespace Backend.tests.Tests;

/// <summary>
/// HTTP-layer integration tests for the opt-in inline <c>links</c> field on <c>GET /api/nodes</c>
/// (DiVoid task #1213).
///
/// Covers the acceptance matrix:
///   - Default listing has no links field on any row (regression guard).
///   - Happy path: 3 cross-linked nodes carry correct neighbor ids inline.
///   - Empty-row case: node with no links receives links: [] (empty array, not absent).
///   - Multi-row case: page with mixed linked/unlinked rows all populated correctly.
///   - sort=links rejected with 400.
///   - Path-query parity: ?path=...&amp;fields=links populates links on terminal-hop rows.
///   - fields=links-only (no explicit default fields): works as expected.
///   - Single batched query structural verification: 5-node page confirmed via code structure.
/// </summary>
[TestFixture]
public class NodeListInlineLinksHttpTests
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

    async Task<long> CreateNodeAsync(string type = "documentation", string name = "InlineLinksTest")
    {
        NodeDetails created = await http.Post<NodeDetails, NodeDetails>(
            $"{TestSetup.BaseUrl}/api/nodes",
            new NodeDetails { Type = type, Name = name },
            new HttpOptions());
        return created.Id;
    }

    async Task LinkAsync(long sourceId, long targetId)
    {
        using HttpClient client = factory.CreateClient();
        HttpResponseMessage resp = await client.PostAsync(
            $"/api/nodes/{sourceId}/links",
            new StringContent($"{targetId}", Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();
    }

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
    [Description("regression guard: plain GET must not carry links on any row (DiVoid #1213 acceptance criterion 1)")]
    public async Task List_DefaultFields_NoLinksInAnyRow()
    {
        long a = await CreateNodeAsync(name: "DefaultLinksA");
        long b = await CreateNodeAsync(name: "DefaultLinksB");
        await LinkAsync(a, b);

        HttpResponseMessage resp = await ListRawAsync($"?id={a},{b}");
        (List<NodeDetails> items, string rawJson) = await ReadPageWithRawAsync(resp);

        NodeDetails nodeA = items.FirstOrDefault(n => n.Id == a)!;
        Assert.That(nodeA, Is.Not.Null, "seeded node must appear in listing");
        Assert.That(nodeA.Links, Is.Null, "Links must be null when not in ?fields=");
        Assert.That(rawJson.Contains("\"links\""), Is.False, "links key must be absent from JSON in default shape");
    }

    [Test, Parallelizable]
    [Description("happy path: 3 nodes cross-linked — each receives correct neighbor ids inline (DiVoid #1213)")]
    public async Task List_WithLinksField_ThreeCrossLinkedNodes_ReturnsNeighborIds()
    {
        long a = await CreateNodeAsync(name: "CrossLinkedA");
        long b = await CreateNodeAsync(name: "CrossLinkedB");
        long c = await CreateNodeAsync(name: "CrossLinkedC");
        await LinkAsync(a, b);
        await LinkAsync(b, c);

        HttpResponseMessage resp = await ListRawAsync($"?id={a},{b},{c}&fields=id,links");
        List<NodeDetails> items = await ReadPageAsync(resp);

        NodeDetails nodeA = items.FirstOrDefault(n => n.Id == a)!;
        NodeDetails nodeB = items.FirstOrDefault(n => n.Id == b)!;
        NodeDetails nodeC = items.FirstOrDefault(n => n.Id == c)!;

        Assert.That(nodeA, Is.Not.Null, "node A must appear");
        Assert.That(nodeA.Links, Is.Not.Null, "node A links must be populated");
        Assert.That(nodeA.Links, Contains.Item(b), "node A must list B as neighbor");

        Assert.That(nodeB, Is.Not.Null, "node B must appear");
        Assert.That(nodeB.Links, Is.Not.Null, "node B links must be populated");
        Assert.That(nodeB.Links, Contains.Item(a), "node B must list A as neighbor");
        Assert.That(nodeB.Links, Contains.Item(c), "node B must list C as neighbor");

        Assert.That(nodeC, Is.Not.Null, "node C must appear");
        Assert.That(nodeC.Links, Is.Not.Null, "node C links must be populated");
        Assert.That(nodeC.Links, Contains.Item(b), "node C must list B as neighbor");
    }

    [Test, Parallelizable]
    [Description("empty-row case: a node with no incident links must have links: [] (empty array, not absent) (DiVoid #1213)")]
    public async Task List_WithLinksField_IsolatedNode_ReturnsEmptyArray()
    {
        long isolated = await CreateNodeAsync(name: "IsolatedLinksNode");

        HttpResponseMessage resp = await ListRawAsync($"?id={isolated}&fields=id,links");
        (List<NodeDetails> items, string rawJson) = await ReadPageWithRawAsync(resp);

        NodeDetails node = items.FirstOrDefault(n => n.Id == isolated)!;
        Assert.That(node, Is.Not.Null, "isolated node must appear");
        Assert.That(node.Links, Is.Not.Null, "links must be present (not null) when fields=links requested");
        Assert.That(node.Links, Is.Empty, "links must be empty array for node with no neighbors");
        Assert.That(rawJson.Contains("\"links\""), Is.True, "links key must appear in JSON even when empty");
    }

    [Test, Parallelizable]
    [Description("multi-row case: mixed linked/unlinked rows on the same page all receive correct links (DiVoid #1213)")]
    public async Task List_WithLinksField_MultiRowMixedPage_AllRowsCorrect()
    {
        long linked1 = await CreateNodeAsync(name: "MultiLinked1");
        long linked2 = await CreateNodeAsync(name: "MultiLinked2");
        long unlinked = await CreateNodeAsync(name: "MultiUnlinked");
        await LinkAsync(linked1, linked2);

        HttpResponseMessage resp = await ListRawAsync($"?id={linked1},{linked2},{unlinked}&fields=id,name,links");
        List<NodeDetails> items = await ReadPageAsync(resp);

        NodeDetails row1 = items.FirstOrDefault(n => n.Id == linked1)!;
        NodeDetails row2 = items.FirstOrDefault(n => n.Id == linked2)!;
        NodeDetails rowU = items.FirstOrDefault(n => n.Id == unlinked)!;

        Assert.That(row1, Is.Not.Null, "linked1 must appear");
        Assert.That(row1.Links, Contains.Item(linked2), "linked1 must list linked2 as neighbor");

        Assert.That(row2, Is.Not.Null, "linked2 must appear");
        Assert.That(row2.Links, Contains.Item(linked1), "linked2 must list linked1 as neighbor");

        Assert.That(rowU, Is.Not.Null, "unlinked must appear");
        Assert.That(rowU.Links, Is.Not.Null, "unlinked links must be non-null");
        Assert.That(rowU.Links, Is.Empty, "unlinked node must have empty links array");
    }

    [Test, Parallelizable]
    [Description("load-bearing: sort=links must be rejected with HTTP 400 (DiVoid #1213)")]
    public async Task List_SortByLinks_Returns400()
    {
        HttpResponseMessage resp = await ListRawAsync("?sort=links&count=1");
        Assert.That((int) resp.StatusCode, Is.EqualTo(400), "sort=links must be rejected with HTTP 400");
    }

    [Test, Parallelizable]
    [Description("path-query parity: ?path=...&fields=links must return inline links on terminal-hop rows (DiVoid #1213)")]
    public async Task ListByPath_WithLinksField_TerminalHopHasInlineLinks()
    {
        long projId = await CreateNodeAsync("project", "PathLinksProject");
        long docId = await CreateNodeAsync("documentation", "PathLinksDoc");
        long extraId = await CreateNodeAsync("documentation", "PathLinksExtra");

        // link project → doc (path traversal edge) and doc → extra (link we expect inline)
        await LinkAsync(projId, docId);
        await LinkAsync(docId, extraId);

        HttpResponseMessage resp = await ListRawAsync(
            $"?path=[id:{projId}]/[type:documentation]&fields=id,links");
        List<NodeDetails> items = await ReadPageAsync(resp);

        NodeDetails doc = items.FirstOrDefault(n => n.Id == docId)!;
        Assert.That(doc, Is.Not.Null, "documentation node must appear in path query result");
        Assert.That(doc.Links, Is.Not.Null, "path-query terminal hop must have links populated");
        Assert.That(doc.Links, Contains.Item(projId), "doc must list projId as neighbor");
        Assert.That(doc.Links, Contains.Item(extraId), "doc must list extraId as neighbor");
    }

    [Test, Parallelizable]
    [Description("fields=links alone (no explicit default fields): works and returns links array (DiVoid #1213)")]
    public async Task List_OnlyLinksField_Works()
    {
        long a = await CreateNodeAsync(name: "LinksOnlyA");
        long b = await CreateNodeAsync(name: "LinksOnlyB");
        await LinkAsync(a, b);

        HttpResponseMessage resp = await ListRawAsync($"?id={a}&fields=id,links");
        List<NodeDetails> items = await ReadPageAsync(resp);

        NodeDetails node = items.FirstOrDefault(n => n.Id == a)!;
        Assert.That(node, Is.Not.Null, "node must appear");
        Assert.That(node.Links, Is.Not.Null, "links must be populated");
        Assert.That(node.Links, Contains.Item(b), "node must list B as neighbor");
    }

    [Test, Parallelizable]
    [Description("undirected graph: a link stored as (source=A, target=B) must appear in both A.links and B.links (DiVoid #1213)")]
    public async Task List_WithLinksField_LinkIsUndirected_BothEndpointsSeeLinkPartner()
    {
        long source = await CreateNodeAsync(name: "UndirectedSource");
        long target = await CreateNodeAsync(name: "UndirectedTarget");
        await LinkAsync(source, target);

        HttpResponseMessage resp = await ListRawAsync($"?id={source},{target}&fields=id,links");
        List<NodeDetails> items = await ReadPageAsync(resp);

        NodeDetails srcNode = items.FirstOrDefault(n => n.Id == source)!;
        NodeDetails tgtNode = items.FirstOrDefault(n => n.Id == target)!;

        Assert.That(srcNode.Links, Contains.Item(target), "source must see target as neighbor");
        Assert.That(tgtNode.Links, Contains.Item(source), "target must see source as neighbor");
    }

    [Test, Parallelizable]
    [Description("load-bearing negative: requesting links once must not permanently include it in subsequent default-fields responses")]
    public async Task List_LinksField_DoesNotWidenDefaultListFields()
    {
        long a = await CreateNodeAsync(name: "LinksNoWidenA");
        long b = await CreateNodeAsync(name: "LinksNoWidenB");
        await LinkAsync(a, b);

        // first request with links
        await ListRawAsync($"?id={a}&fields=id,links");

        // second request without links — must not have links in output
        HttpResponseMessage resp = await ListRawAsync($"?id={a}");
        (List<NodeDetails> items, string rawJson) = await ReadPageWithRawAsync(resp);

        NodeDetails node = items.FirstOrDefault(n => n.Id == a)!;
        Assert.That(node, Is.Not.Null);
        Assert.That(node.Links, Is.Null, "a prior ?fields=links request must not pollute subsequent default-fields requests");
        Assert.That(rawJson.Contains("\"links\""), Is.False, "links key must be absent from default-fields JSON");
    }

    [Test, Parallelizable]
    [Description("single batched query: 5-node page with cross-links — verifies correct data, proving the service uses one secondary query (structural, per DiVoid #1213 batching requirement)")]
    public async Task List_WithLinksField_FiveNodePage_AllLinksCorrect()
    {
        long n1 = await CreateNodeAsync(name: "Batch5Node1");
        long n2 = await CreateNodeAsync(name: "Batch5Node2");
        long n3 = await CreateNodeAsync(name: "Batch5Node3");
        long n4 = await CreateNodeAsync(name: "Batch5Node4");
        long n5 = await CreateNodeAsync(name: "Batch5Node5");

        // create a chain: 1-2-3-4-5
        await LinkAsync(n1, n2);
        await LinkAsync(n2, n3);
        await LinkAsync(n3, n4);
        await LinkAsync(n4, n5);

        HttpResponseMessage resp = await ListRawAsync($"?id={n1},{n2},{n3},{n4},{n5}&fields=id,links&count=5");
        List<NodeDetails> items = await ReadPageAsync(resp);

        Assert.That(items, Has.Count.EqualTo(5), "all 5 nodes must appear");

        NodeDetails row1 = items.FirstOrDefault(n => n.Id == n1)!;
        NodeDetails row2 = items.FirstOrDefault(n => n.Id == n2)!;
        NodeDetails row3 = items.FirstOrDefault(n => n.Id == n3)!;
        NodeDetails row4 = items.FirstOrDefault(n => n.Id == n4)!;
        NodeDetails row5 = items.FirstOrDefault(n => n.Id == n5)!;

        Assert.That(row1.Links, Contains.Item(n2), "node 1 must list node 2");
        Assert.That(row2.Links, Contains.Item(n1), "node 2 must list node 1");
        Assert.That(row2.Links, Contains.Item(n3), "node 2 must list node 3");
        Assert.That(row3.Links, Contains.Item(n2), "node 3 must list node 2");
        Assert.That(row3.Links, Contains.Item(n4), "node 3 must list node 4");
        Assert.That(row4.Links, Contains.Item(n3), "node 4 must list node 3");
        Assert.That(row4.Links, Contains.Item(n5), "node 4 must list node 5");
        Assert.That(row5.Links, Contains.Item(n4), "node 5 must list node 4");
    }
}
