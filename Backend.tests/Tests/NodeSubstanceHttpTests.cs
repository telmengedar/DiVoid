using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Http;
using Pooshit.Json;

namespace Backend.tests.Tests;

/// <summary>
/// HTTP-layer integration tests for the client-filled <c>substance</c> property on nodes.
/// </summary>
[TestFixture, Parallelizable]
public class NodeSubstanceHttpTests
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

    Task<NodeDetails> CreateNodeAsync(string name, string? substance = null)
        => http.Post<NodeDetails, NodeDetails>(
            $"{TestSetup.BaseUrl}/api/nodes",
            new NodeDetails { Type = "documentation", Name = name, Substance = substance },
            new HttpOptions());

    Task<HttpResponseMessage> GetRawAsync(long nodeId)
        => http.Get<HttpResponseMessage>($"{TestSetup.BaseUrl}/api/nodes/{nodeId}");

    async Task<(NodeDetails Node, string RawJson)> GetWithRawAsync(long nodeId)
    {
        HttpResponseMessage resp = await GetRawAsync(nodeId);
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync();
        return (Json.Read<NodeDetails>(json), json);
    }

    async Task<(List<NodeDetails> Items, string RawJson)> ListWithRawAsync(string query)
    {
        HttpResponseMessage resp = await http.Get<HttpResponseMessage>($"{TestSetup.BaseUrl}/api/nodes{query}");
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync();
        Page<NodeDetails> page = Json.Read<Page<NodeDetails>>(json);
        return (page.Result?.ToList() ?? [], json);
    }

    Task<HttpResponseMessage> PatchAsync(long nodeId, params PatchOperation[] ops)
        => http.Patch<PatchOperation[], HttpResponseMessage>($"{TestSetup.BaseUrl}/api/nodes/{nodeId}", ops);

    async Task UploadTextAsync(long nodeId, string contentType, string body)
    {
        using HttpClient client = factory.CreateClient();
        ByteArrayContent uploadBody = new(Encoding.UTF8.GetBytes(body));
        uploadBody.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        HttpResponseMessage resp = await client.PostAsync($"/api/nodes/{nodeId}/content", uploadBody);
        resp.EnsureSuccessStatusCode();
    }

    [Test, Parallelizable]
    [Description("S1 — guards the positional Columns/Values pairing in CreateNode: a substance supplied on POST must come back from a separate GET, and the name must not have absorbed it.")]
    public async Task CreateNode_WithSubstance_SubstancePersistedToDatabase()
    {
        NodeDetails created = await CreateNodeAsync("S1_PersistName", "S1|k=v;n=42");

        Assert.That(created.Id, Is.GreaterThan(0), "POST must return a valid node id");

        (NodeDetails fetched, string _) = await GetWithRawAsync(created.Id);

        Assert.Multiple(() => {
            Assert.That(fetched.Substance, Is.EqualTo("S1|k=v;n=42"),
                "substance supplied on POST must survive a separate GET — absent means the column is missing from the INSERT, "
                + "a different value means the Columns/Values pair is mis-ordered");
            Assert.That(fetched.Name, Is.EqualTo("S1_PersistName"),
                "name must still be the posted name — a mis-ordered Columns/Values pair writes substance into it");
        });
    }

    [Test, Parallelizable]
    [Description("S2 — a node created without substance reads back null and the key is omitted from the response.")]
    public async Task CreateNode_WithoutSubstance_SubstanceIsNullAfterGet()
    {
        NodeDetails created = await CreateNodeAsync("S2_NoSubstance");

        (NodeDetails fetched, string rawJson) = await GetWithRawAsync(created.Id);

        Assert.Multiple(() => {
            Assert.That(fetched.Substance, Is.Null,
                "a node created without substance must read back null — any value means a server-side default was introduced");
            Assert.That(rawJson.Contains("\"substance\""), Is.False,
                "a null substance must be omitted from the JSON body entirely");
        });
    }

    [Test, Parallelizable]
    [Description("S3 — the update verb: PATCH replace /substance requires [AllowPatch] on Node.Substance and writes through to the row.")]
    public async Task Patch_ReplaceSubstance_UpdatesField()
    {
        NodeDetails created = await CreateNodeAsync("S3_PatchTarget", "S3|before");

        HttpResponseMessage resp = await PatchAsync(created.Id,
            new PatchOperation { Op = "replace", Path = "/substance", Value = "S3|after" });

        Assert.That((int) resp.StatusCode, Is.EqualTo(200),
            "PATCH replace /substance must return 200 — 400 means [AllowPatch] is missing from Node.Substance");

        (NodeDetails fetched, string _) = await GetWithRawAsync(created.Id);

        Assert.That(fetched.Substance, Is.EqualTo("S3|after"),
            "the patched value must survive a subsequent GET");
    }

    [Test, Parallelizable]
    [Description("S4 — the delete verb: replace /substance with a null value clears the column and the key disappears from the response.")]
    public async Task Patch_ReplaceSubstance_WithNull_ClearsValue()
    {
        NodeDetails created = await CreateNodeAsync("S4_ClearTarget", "S4|present");
        Assert.That(created.Substance, Is.EqualTo("S4|present"),
            "precondition: the node must start out carrying a substance");

        HttpResponseMessage resp = await PatchAsync(created.Id,
            new PatchOperation { Op = "replace", Path = "/substance", Value = null });

        Assert.That((int) resp.StatusCode, Is.EqualTo(200),
            "PATCH replace /substance with a null value must return 200");

        (NodeDetails fetched, string rawJson) = await GetWithRawAsync(created.Id);

        Assert.Multiple(() => {
            Assert.That(fetched.Substance, Is.Null,
                "replace with a null value must clear the stored substance");
            Assert.That(rawJson.Contains("\"substance\""), Is.False,
                "a cleared substance must be omitted from the JSON body, not returned as an empty string");
        });
    }

    [Test, Parallelizable]
    [Description("S5 — GET /api/nodes/{id} returns substance without any ?fields= opt-in.")]
    public async Task GetById_ReturnsSubstanceInline()
    {
        NodeDetails created = await CreateNodeAsync("S5_InlineRead", "S5|inline");

        (NodeDetails fetched, string rawJson) = await GetWithRawAsync(created.Id);

        Assert.Multiple(() => {
            Assert.That(rawJson.Contains("\"substance\""), Is.True,
                "GET /api/nodes/{id} takes no field selection — the substance key must be present without one");
            Assert.That(fetched.Substance, Is.EqualTo("S5|inline"),
                "the inline value must be the stored substance");
        });
    }

    [Test, Parallelizable]
    [Description("S6 — invariant I4: a default listing omits substance even when the node carries one.")]
    public async Task ListPaged_WithoutFieldsOptIn_OmitsSubstance()
    {
        NodeDetails created = await CreateNodeAsync("S6_DefaultListing", "S6|not-in-default-listing");

        (NodeDetails byId, string _) = await GetWithRawAsync(created.Id);
        Assert.That(byId.Substance, Is.EqualTo("S6|not-in-default-listing"),
            "precondition: the node must actually carry a substance, or the omission below proves nothing");

        (List<NodeDetails> items, string rawJson) = await ListWithRawAsync($"?id={created.Id}");

        NodeDetails? row = items.FirstOrDefault(n => n.Id == created.Id);
        Assert.Multiple(() => {
            Assert.That(row, Is.Not.Null, "the seeded node must appear in the default listing");
            Assert.That(row!.Name, Is.EqualTo("S6_DefaultListing"),
                "the default listing must still carry its default fields");
            Assert.That(rawJson.Contains("\"substance\""), Is.False,
                "the default listing must not carry substance — present means it was added to DefaultListFields");
        });
    }

    [Test, Parallelizable]
    [Description("S7 — invariants I1/I2: ?fields=id,substance returns substance and no content.")]
    public async Task ListPaged_FieldsSubstance_ReturnsSubstanceAndNoContent()
    {
        NodeDetails created = await CreateNodeAsync("S7_FieldsOptIn", "S7|requested");
        await UploadTextAsync(created.Id, "text/markdown", "# S7 prose body that must not travel");

        (List<NodeDetails> items, string rawJson) = await ListWithRawAsync($"?id={created.Id}&fields=id,substance");

        NodeDetails? row = items.FirstOrDefault(n => n.Id == created.Id);
        Assert.Multiple(() => {
            Assert.That(row, Is.Not.Null, "the seeded node must appear when substance is requested");
            Assert.That(row!.Substance, Is.EqualTo("S7|requested"),
                "the requested substance must arrive — a wrong value means substance is aliased onto another mapping");
            Assert.That(row!.Content, Is.Null,
                "content must not be materialised when only substance was requested");
            Assert.That(rawJson.Contains("\"content\""), Is.False,
                "the content key must be absent from the row — present means substance drags the prose body onto the wire");
            Assert.That(rawJson.Contains("S7 prose body that must not travel"), Is.False,
                "the content bytes must not appear anywhere in the response body");
        });
    }

    [Test, Parallelizable]
    [Description("S8 — invariant I2: writing substance must not read, rewrite or truncate the content blob.")]
    public async Task Patch_ReplaceSubstance_DoesNotAlterContent()
    {
        NodeDetails created = await CreateNodeAsync("S8_ContentUntouched", "S8|before");
        await UploadTextAsync(created.Id, "text/markdown", "# S8 body\n\nunchanged prose");

        HttpResponseMessage patchResp = await PatchAsync(created.Id,
            new PatchOperation { Op = "replace", Path = "/substance", Value = "S8|after" });
        Assert.That((int) patchResp.StatusCode, Is.EqualTo(200), "PATCH replace /substance must return 200");

        HttpResponseMessage contentResp = await http.Get<HttpResponseMessage>(
            $"{TestSetup.BaseUrl}/api/nodes/{created.Id}/content");
        contentResp.EnsureSuccessStatusCode();
        byte[] bytes = await contentResp.Content.ReadAsByteArrayAsync();

        (NodeDetails fetched, string _) = await GetWithRawAsync(created.Id);

        Assert.Multiple(() => {
            Assert.That(fetched.Substance, Is.EqualTo("S8|after"),
                "precondition: the substance write must actually have happened");
            Assert.That(Encoding.UTF8.GetString(bytes), Is.EqualTo("# S8 body\n\nunchanged prose"),
                "the content blob must be byte-identical after a substance write");
            Assert.That(fetched.ContentType, Is.EqualTo("text/markdown"),
                "the content type must be untouched by a substance write");
        });
    }

    [Test, Parallelizable]
    [Description("S10 — the column is unbounded text: a 50 KB substance round-trips unaltered rather than being silently truncated to a bounded varchar.")]
    public async Task CreateNode_WithLargeSubstance_RoundTripsUnaltered()
    {
        string large = string.Concat(Enumerable.Repeat("S10|k=v;", 6400));
        Assert.That(large, Has.Length.EqualTo(51200), "precondition: the payload must exceed 50 KB");

        NodeDetails created = await CreateNodeAsync("S10_LargeSubstance", large);

        (NodeDetails fetched, string _) = await GetWithRawAsync(created.Id);

        Assert.Multiple(() => {
            Assert.That(fetched.Substance, Has.Length.EqualTo(51200),
                "a 50 KB substance must come back at full length — a shorter value means the column was mapped to a bounded type");
            Assert.That(fetched.Substance, Is.EqualTo(large),
                "the stored substance must be byte-for-byte what was posted");
        });
    }
}
