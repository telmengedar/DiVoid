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
/// HTTP-layer integration tests verifying that <c>contentType</c> is surfaced on
/// node-listing responses (task #102).
///
/// Covers:
///   - Node with content → <c>contentType</c> present in listing.
///   - Node without content → <c>contentType</c> absent (null/omitted).
///   - Explicit <c>?fields=</c> excluding <c>contentType</c> → field absent.
///   - <c>?sort=contentType</c> → items ordered correctly.
/// </summary>
[TestFixture]
public class NodeListContentTypeHttpTests
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

    async Task<long> CreateNodeAsync(string type = "task", string name = "ContentTypeTestNode")
    {
        NodeDetails created = await http.Post<NodeDetails, NodeDetails>(
            $"{TestSetup.BaseUrl}/api/nodes",
            new NodeDetails { Type = type, Name = name },
            new HttpOptions());
        return created.Id;
    }

    async Task UploadContentAsync(long nodeId, string contentType, string body)
    {
        using HttpClient client = factory.CreateClient();
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        HttpContent uploadBody = new ByteArrayContent(bytes);
        uploadBody.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        HttpResponseMessage resp = await client.PostAsync($"/api/nodes/{nodeId}/content", uploadBody);
        resp.EnsureSuccessStatusCode();
    }

    Task<HttpResponseMessage> ListAsync(string query = "")
        => http.Get<HttpResponseMessage>($"{TestSetup.BaseUrl}/api/nodes{query}");

    static async Task<List<NodeDetails>> ReadPageAsync(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync();
        Page<NodeDetails> page = Json.Read<Page<NodeDetails>>(json);
        return page.Result?.ToList() ?? [];
    }

    // -----------------------------------------------------------------------
    // contentType presence on nodes that have content
    // -----------------------------------------------------------------------

    [Test]
    public async Task List_NodeWithContent_IncludesContentTypeField()
    {
        long id = await CreateNodeAsync(name: "NodeWithContent");
        await UploadContentAsync(id, "text/markdown", "# Hello");

        HttpResponseMessage resp = await ListAsync($"?id={id}");
        List<NodeDetails> items = await ReadPageAsync(resp);

        NodeDetails node = items.FirstOrDefault(n => n.Id == id)!;
        Assert.That(node, Is.Not.Null, "expected node to appear in listing");
        Assert.That(node.ContentType, Is.EqualTo("text/markdown"),
            "listing must include contentType for nodes that have content");
    }

    // -----------------------------------------------------------------------
    // contentType absent on nodes that have no content
    // -----------------------------------------------------------------------

    [Test]
    public async Task List_NodeWithoutContent_OmitsContentTypeField()
    {
        long id = await CreateNodeAsync(name: "NodeWithoutContent");

        HttpResponseMessage resp = await ListAsync($"?id={id}");
        List<NodeDetails> items = await ReadPageAsync(resp);

        NodeDetails node = items.FirstOrDefault(n => n.Id == id)!;
        Assert.That(node, Is.Not.Null, "expected node to appear in listing");
        Assert.That(node.ContentType, Is.Null.Or.Empty,
            "listing must omit contentType for nodes that have no content");
    }

    // -----------------------------------------------------------------------
    // explicit ?fields= that excludes contentType
    // -----------------------------------------------------------------------

    [Test]
    public async Task List_WithExplicitFieldsNotIncludingContentType_OmitsField()
    {
        long id = await CreateNodeAsync(name: "ExplicitFieldsNode");
        await UploadContentAsync(id, "application/json", "{}");

        HttpResponseMessage resp = await ListAsync($"?id={id}&fields=id,name");
        List<NodeDetails> items = await ReadPageAsync(resp);

        NodeDetails node = items.FirstOrDefault(n => n.Id == id)!;
        Assert.That(node, Is.Not.Null, "expected node to appear in listing");
        Assert.That(node.ContentType, Is.Null.Or.Empty,
            "?fields=id,name must not include contentType even when node has content");
    }

    // -----------------------------------------------------------------------
    // ?sort=contentType orders items correctly
    // -----------------------------------------------------------------------

    [Test]
    public async Task List_SortByContentType_OrdersCorrectly()
    {
        long idA = await CreateNodeAsync(name: "SortNodeA");
        long idB = await CreateNodeAsync(name: "SortNodeB");
        await UploadContentAsync(idA, "application/json", "{}");
        await UploadContentAsync(idB, "text/markdown", "# hi");

        // application/json sorts before text/markdown ascending
        HttpResponseMessage resp = await ListAsync($"?id={idA},{idB}&sort=contentType");
        List<NodeDetails> items = await ReadPageAsync(resp);

        List<NodeDetails> relevant = items.Where(n => n.Id == idA || n.Id == idB).ToList();
        Assert.That(relevant.Count, Is.EqualTo(2), "both nodes must appear in listing");
        Assert.That(relevant[0].Id, Is.EqualTo(idA),
            "application/json must sort before text/markdown ascending");
        Assert.That(relevant[1].Id, Is.EqualTo(idB),
            "text/markdown must sort after application/json ascending");
    }

}
