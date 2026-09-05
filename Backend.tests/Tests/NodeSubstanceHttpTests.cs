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

    async Task<HttpResponseMessage> PatchContentAsync(long nodeId, string editsJson)
    {
        using HttpClient client = factory.CreateClient();
        HttpRequestMessage request = new(HttpMethod.Patch, $"/api/nodes/{nodeId}/content")
        {
            Content = new StringContent(editsJson, Encoding.UTF8, "application/json")
        };
        return await client.SendAsync(request);
    }

    async Task<string> GetContentStringAsync(long nodeId)
    {
        using HttpClient client = factory.CreateClient();
        HttpResponseMessage resp = await client.GetAsync($"/api/nodes/{nodeId}/content");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
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
        NodeDetails created = await CreateNodeAsync("S7_FieldsOptIn");
        await UploadTextAsync(created.Id, "text/markdown", "# S7 prose body that must not travel");
        HttpResponseMessage setResp = await PatchAsync(created.Id,
            new PatchOperation { Op = "replace", Path = "/substance", Value = "S7|requested" });
        Assert.That((int) setResp.StatusCode, Is.EqualTo(200),
            "precondition: the substance must be written after the content upload, which clears it");

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

    [Test, Parallelizable]
    [Description("C1 — a content upload clears the node's substance to NULL, so the key is absent from a later GET.")]
    public async Task UploadContent_ClearsSubstance()
    {
        NodeDetails created = await CreateNodeAsync("C1_UploadClears", "C1|stale-after-upload");

        (NodeDetails before, string beforeJson) = await GetWithRawAsync(created.Id);
        Assert.Multiple(() => {
            Assert.That(before.Substance, Is.EqualTo("C1|stale-after-upload"),
                "precondition: the node must carry a substance before the upload, or an absent key afterwards proves nothing");
            Assert.That(beforeJson.Contains("\"substance\""), Is.True,
                "precondition: the substance key must be present before the upload");
        });

        await UploadTextAsync(created.Id, "text/markdown", "# C1 body uploaded after the substance was written");

        (NodeDetails after, string afterJson) = await GetWithRawAsync(created.Id);
        Assert.Multiple(() => {
            Assert.That(after.Substance, Is.Null,
                "the content upload must clear the substance - a surviving value means the clear term is missing from the upload UPDATE, "
                + "or was folded into the embedding helper whose capability early-return fires on SQLite");
            Assert.That(afterJson.Contains("\"substance\""), Is.False,
                "the cleared substance must be NULL and therefore omitted from the JSON - a present key means the clear wrote an empty string");
        });
    }

    [Test, Parallelizable]
    [Description("C2 — a content range edit clears the node's substance to NULL.")]
    public async Task PatchContent_ClearsSubstance()
    {
        NodeDetails created = await CreateNodeAsync("C2_PatchContentClears");
        await UploadTextAsync(created.Id, "text/plain", "alpha\nbravo\ncharlie\n");

        HttpResponseMessage setResp = await PatchAsync(created.Id,
            new PatchOperation { Op = "replace", Path = "/substance", Value = "C2|stale-after-edit" });
        Assert.That((int) setResp.StatusCode, Is.EqualTo(200), "precondition: the substance write must succeed");

        (NodeDetails before, string beforeJson) = await GetWithRawAsync(created.Id);
        Assert.Multiple(() => {
            Assert.That(before.Substance, Is.EqualTo("C2|stale-after-edit"),
                "precondition: the node must carry a substance before the edit, or an absent key afterwards proves nothing");
            Assert.That(beforeJson.Contains("\"substance\""), Is.True,
                "precondition: the substance key must be present before the edit");
        });

        HttpResponseMessage resp = await PatchContentAsync(created.Id,
            """[ { "unit": "line", "start": 1, "length": 1, "value": "BRAVO\n" } ]""");
        Assert.That((int) resp.StatusCode, Is.EqualTo(200), "the content edit must succeed");

        (NodeDetails after, string afterJson) = await GetWithRawAsync(created.Id);
        string storedContent = await GetContentStringAsync(created.Id);
        Assert.Multiple(() => {
            Assert.That(storedContent, Is.EqualTo("alpha\nBRAVO\ncharlie\n"),
                "precondition: the edit must actually have changed the stored content");
            Assert.That(after.Substance, Is.Null,
                "the content edit must clear the substance - a surviving value means the clear term is missing from the patch UPDATE, "
                + "or was folded into the embedding helper whose capability early-return fires on SQLite");
            Assert.That(afterJson.Contains("\"substance\""), Is.False,
                "the cleared substance must be NULL and therefore omitted from the JSON - a present key means the clear wrote an empty string");
        });
    }

    [Test, Parallelizable]
    [Description("C3 — the PATCH /content response body itself reports the clear, without a follow-up GET.")]
    public async Task PatchContent_Response_OmitsClearedSubstance()
    {
        NodeDetails created = await CreateNodeAsync("C3_PatchContentResponse");
        await UploadTextAsync(created.Id, "text/plain", "one\ntwo\nthree\n");

        HttpResponseMessage setResp = await PatchAsync(created.Id,
            new PatchOperation { Op = "replace", Path = "/substance", Value = "C3|reported-gone" });
        Assert.That((int) setResp.StatusCode, Is.EqualTo(200), "precondition: the substance write must succeed");

        (NodeDetails before, string beforeJson) = await GetWithRawAsync(created.Id);
        Assert.Multiple(() => {
            Assert.That(before.Substance, Is.EqualTo("C3|reported-gone"),
                "precondition: the node must carry a substance before the edit");
            Assert.That(beforeJson.Contains("\"substance\""), Is.True,
                "precondition: the substance key must be present before the edit");
        });

        HttpResponseMessage resp = await PatchContentAsync(created.Id,
            """[ { "unit": "line", "start": 1, "length": 1, "value": "TWO\n" } ]""");
        Assert.That((int) resp.StatusCode, Is.EqualTo(200), "the content edit must succeed");

        string responseJson = await resp.Content.ReadAsStringAsync();
        NodeDetails returned = Json.Read<NodeDetails>(responseJson);
        Assert.Multiple(() => {
            Assert.That(returned.Id, Is.EqualTo(created.Id),
                "precondition: the response must be the edited node, or the absent key below is read off the wrong body");
            Assert.That(returned.Substance, Is.Null,
                "the PATCH /content response must already report the cleared substance - a value here means the response was "
                + "assembled before the write rather than re-read after it");
            Assert.That(responseJson.Contains("\"substance\""), Is.False,
                "the response body must omit the substance key entirely");
        });
    }

    [Test, Parallelizable]
    [Description("C4 — the clear is unconditional: re-uploading byte-identical content still clears a substance written in between.")]
    public async Task UploadContent_WithByteIdenticalContent_StillClearsSubstance()
    {
        const string body = "# C4 identical body\n\nunchanged between the two uploads";

        NodeDetails created = await CreateNodeAsync("C4_IdenticalUpload");
        await UploadTextAsync(created.Id, "text/markdown", body);

        HttpResponseMessage setResp = await PatchAsync(created.Id,
            new PatchOperation { Op = "replace", Path = "/substance", Value = "C4|written-between-uploads" });
        Assert.That((int) setResp.StatusCode, Is.EqualTo(200), "precondition: the substance write must succeed");

        (NodeDetails before, string beforeJson) = await GetWithRawAsync(created.Id);
        Assert.Multiple(() => {
            Assert.That(before.Substance, Is.EqualTo("C4|written-between-uploads"),
                "precondition: the substance must be present after the first upload and before the second");
            Assert.That(beforeJson.Contains("\"substance\""), Is.True,
                "precondition: the substance key must be present before the second upload");
        });

        await UploadTextAsync(created.Id, "text/markdown", body);

        (NodeDetails after, string afterJson) = await GetWithRawAsync(created.Id);
        string storedContent = await GetContentStringAsync(created.Id);
        Assert.Multiple(() => {
            Assert.That(storedContent, Is.EqualTo(body),
                "precondition: the second upload must have stored the same bytes");
            Assert.That(after.Substance, Is.Null,
                "a byte-identical re-upload must still clear the substance - a surviving value means a change-comparison was added to the upload path");
            Assert.That(afterJson.Contains("\"substance\""), Is.False,
                "the cleared substance must be omitted from the JSON");
        });
    }

    [Test, Parallelizable]
    [Description("C5 — the clear is unconditional: a range edit that replaces a line with its own current text still clears the substance.")]
    public async Task PatchContent_WithNoOpEdit_StillClearsSubstance()
    {
        const string body = "alpha\nbravo\ncharlie\n";

        NodeDetails created = await CreateNodeAsync("C5_NoOpEdit");
        await UploadTextAsync(created.Id, "text/plain", body);

        HttpResponseMessage setResp = await PatchAsync(created.Id,
            new PatchOperation { Op = "replace", Path = "/substance", Value = "C5|survives-nothing" });
        Assert.That((int) setResp.StatusCode, Is.EqualTo(200), "precondition: the substance write must succeed");

        (NodeDetails before, string beforeJson) = await GetWithRawAsync(created.Id);
        Assert.Multiple(() => {
            Assert.That(before.Substance, Is.EqualTo("C5|survives-nothing"),
                "precondition: the node must carry a substance before the no-op edit");
            Assert.That(beforeJson.Contains("\"substance\""), Is.True,
                "precondition: the substance key must be present before the no-op edit");
        });

        HttpResponseMessage resp = await PatchContentAsync(created.Id,
            """[ { "unit": "line", "start": 1, "length": 1, "value": "bravo\n" } ]""");
        Assert.That((int) resp.StatusCode, Is.EqualTo(200), "the no-op content edit must succeed");

        (NodeDetails after, string afterJson) = await GetWithRawAsync(created.Id);
        string storedContent = await GetContentStringAsync(created.Id);
        Assert.Multiple(() => {
            Assert.That(storedContent, Is.EqualTo(body),
                "precondition: the edit must have produced byte-identical content, or this is not a no-op edit");
            Assert.That(after.Substance, Is.Null,
                "a no-op range edit must still clear the substance - a surviving value means a change-comparison was added to the patch path");
            Assert.That(afterJson.Contains("\"substance\""), Is.False,
                "the cleared substance must be omitted from the JSON");
        });
    }

    [Test, Parallelizable]
    [Description("C6 — the clear is keyed on content writes only: a name PATCH leaves the substance intact.")]
    public async Task Patch_ReplaceName_LeavesSubstanceIntact()
    {
        NodeDetails created = await CreateNodeAsync("C6_NamePatch", "C6|must-survive-a-rename");

        HttpResponseMessage resp = await PatchAsync(created.Id,
            new PatchOperation { Op = "replace", Path = "/name", Value = "C6_NamePatch_Renamed" });
        Assert.That((int) resp.StatusCode, Is.EqualTo(200), "PATCH replace /name must return 200");

        (NodeDetails fetched, string _) = await GetWithRawAsync(created.Id);
        Assert.Multiple(() => {
            Assert.That(fetched.Name, Is.EqualTo("C6_NamePatch_Renamed"),
                "precondition: the rename must actually have happened");
            Assert.That(fetched.Substance, Is.EqualTo("C6|must-survive-a-rename"),
                "a name PATCH must leave the substance intact - a null here means the clear was placed on the shared node-PATCH path");
        });
    }

    [Test, Parallelizable]
    [Description("C8 — PATCH /api/nodes/{id} cannot reach content, so the content-write inventory the clear covers stays closed at two paths.")]
    public async Task Patch_ReplaceContent_IsRejected()
    {
        NodeDetails created = await CreateNodeAsync("C8_ContentNotPatchable");
        await UploadTextAsync(created.Id, "text/plain", "C8 original body");
        HttpResponseMessage setResp = await PatchAsync(created.Id,
            new PatchOperation { Op = "replace", Path = "/substance", Value = "C8|untouched" });
        Assert.That((int) setResp.StatusCode, Is.EqualTo(200),
            "precondition: the substance must be written after the content upload, which clears it");

        HttpResponseMessage resp = await PatchAsync(created.Id,
            new PatchOperation { Op = "replace", Path = "/content", Value = "C8 smuggled body" });

        Assert.That((int) resp.StatusCode, Is.EqualTo(400),
            "PATCH replace /content must be rejected - a 200 means [AllowPatch] reached Node.Content and a third content-write path exists with no clear");

        (NodeDetails fetched, string _) = await GetWithRawAsync(created.Id);
        string storedContent = await GetContentStringAsync(created.Id);
        Assert.Multiple(() => {
            Assert.That(storedContent, Is.EqualTo("C8 original body"),
                "the rejected patch must leave the stored content untouched");
            Assert.That(fetched.Substance, Is.EqualTo("C8|untouched"),
                "the rejected patch must leave the substance untouched");
        });
    }
}
