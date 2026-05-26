using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
/// HTTP-layer integration tests for the opt-in inline <c>content</c> field on <c>GET /api/nodes</c>
/// (task #1180, architecture doc #1182).
///
/// Covers the full acceptance matrix from arch doc §14 step 7:
///   - Default listing has no content/contentTruncated/contentLength fields.
///   - Text content (text/* and application/json) returned as UTF-8 string.
///   - Binary content (image/png, application/octet-stream) returned as base64.
///   - Empty-content rows omit content, contentTruncated, contentLength entirely.
///   - Truncation at 64 KiB with flags for text and binary paths.
///   - UTF-8 multi-byte boundary: truncation backs up to last complete code-point.
///   - Invalid UTF-8 in text-typed content silently demoted to base64.
///   - Mixed page (text + binary + empty) rendered correctly.
///   - sort=content rejected with 400.
///   - Path-query parity: ?path=...&amp;fields=content works identically.
///   - contentType auto-included when content is requested without it.
///   - Load-bearing negative test: bytes column not fetched by default.
///   - Load-bearing substitution probe (documented in PR body).
/// </summary>
[TestFixture]
public class NodeListInlineContentHttpTests
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

    async Task<long> CreateNodeAsync(string type = "documentation", string name = "InlineContentTest")
    {
        NodeDetails created = await http.Post<NodeDetails, NodeDetails>(
            $"{TestSetup.BaseUrl}/api/nodes",
            new NodeDetails { Type = type, Name = name },
            new HttpOptions());
        return created.Id;
    }

    async Task UploadBytesAsync(long nodeId, string contentType, byte[] bytes)
    {
        using HttpClient client = factory.CreateClient();
        ByteArrayContent uploadBody = new(bytes);
        uploadBody.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        HttpResponseMessage resp = await client.PostAsync($"/api/nodes/{nodeId}/content", uploadBody);
        resp.EnsureSuccessStatusCode();
    }

    Task UploadTextAsync(long nodeId, string contentType, string body)
        => UploadBytesAsync(nodeId, contentType, Encoding.UTF8.GetBytes(body));

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
    [Description("guards the default-unchanged invariant: plain GET must not carry content/contentTruncated/contentLength on any row (task #1180 acceptance criterion 1)")]
    public async Task List_DefaultFields_NoContentInAnyRow()
    {
        long id = await CreateNodeAsync(name: "DefaultFieldsNode");
        await UploadTextAsync(id, "text/markdown", "## body");

        HttpResponseMessage resp = await ListRawAsync($"?id={id}");
        (List<NodeDetails> items, string rawJson) = await ReadPageWithRawAsync(resp);

        NodeDetails node = items.FirstOrDefault(n => n.Id == id)!;
        Assert.That(node, Is.Not.Null, "seeded node must appear in listing");
        Assert.That(node.Content, Is.Null, "Content must be null when not in ?fields=");
        Assert.That(node.ContentTruncated, Is.Null, "ContentTruncated must be null when not in ?fields=");
        Assert.That(node.ContentLength, Is.Null, "ContentLength must be null when not in ?fields=");
        Assert.That(rawJson.Contains("\"contentTruncated\""), Is.False, "contentTruncated key must be absent from JSON");
        Assert.That(rawJson.Contains("\"contentLength\""), Is.False, "contentLength key must be absent from JSON");
    }

    [Test, Parallelizable]
    public async Task List_WithContentField_TextNode_ReturnsUtf8String()
    {
        long id = await CreateNodeAsync(name: "TextInlineNode");
        string body = "## Hello\n\nworld";
        await UploadTextAsync(id, "text/markdown", body);

        HttpResponseMessage resp = await ListRawAsync($"?id={id}&fields=id,content");
        (List<NodeDetails> items, string rawJson) = await ReadPageWithRawAsync(resp);

        NodeDetails node = items.FirstOrDefault(n => n.Id == id)!;
        Assert.That(node, Is.Not.Null);
        Assert.That(node.Content, Is.EqualTo(body), "text content must be returned as UTF-8 string");
        Assert.That(node.ContentTruncated, Is.Null, "no truncation for a small body");
        Assert.That(rawJson.Contains("\"contentTruncated\""), Is.False, "contentTruncated key must be absent when not truncated");
    }

    [Test, Parallelizable]
    public async Task List_WithContentField_ApplicationJsonNode_ReturnsUtf8String()
    {
        long id = await CreateNodeAsync(name: "AppJsonInlineNode");
        string body = "{\"key\":\"value\"}";
        await UploadTextAsync(id, "application/json", body);

        HttpResponseMessage resp = await ListRawAsync($"?id={id}&fields=id,content");
        List<NodeDetails> items = await ReadPageAsync(resp);

        NodeDetails node = items.FirstOrDefault(n => n.Id == id)!;
        Assert.That(node, Is.Not.Null);
        Assert.That(node.Content, Is.EqualTo(body), "application/json is text — must be returned as string");
    }

    [Test, Parallelizable]
    public async Task List_WithContentField_BinaryNode_ReturnsBase64()
    {
        long id = await CreateNodeAsync(name: "BinaryInlineNode");
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        await UploadBytesAsync(id, "image/png", bytes);

        HttpResponseMessage resp = await ListRawAsync($"?id={id}&fields=id,content");
        List<NodeDetails> items = await ReadPageAsync(resp);

        NodeDetails node = items.FirstOrDefault(n => n.Id == id)!;
        Assert.That(node, Is.Not.Null);
        Assert.That(node.Content, Is.Not.Null.And.Not.Empty, "binary content must be returned as base64 string");
        byte[] roundTripped = Convert.FromBase64String(node.Content!);
        Assert.That(roundTripped, Is.EqualTo(bytes), "base64 round-trip must recover original bytes");
    }

    [Test, Parallelizable]
    public async Task List_WithContentField_EmptyContentNode_OmitsContentFields()
    {
        long id = await CreateNodeAsync(name: "EmptyContentNode");

        HttpResponseMessage resp = await ListRawAsync($"?id={id}&fields=id,content");
        (List<NodeDetails> items, string rawJson) = await ReadPageWithRawAsync(resp);

        NodeDetails node = items.FirstOrDefault(n => n.Id == id)!;
        Assert.That(node, Is.Not.Null);
        Assert.That(node.Content, Is.Null, "content must be absent for node with no content");
        Assert.That(rawJson.Contains("\"contentTruncated\""), Is.False, "contentTruncated key must be absent");
        Assert.That(rawJson.Contains("\"contentLength\""), Is.False, "contentLength key must be absent");
    }

    [Test, Parallelizable]
    public async Task List_WithContentField_OversizeTextNode_TruncatesAtUtf8Boundary()
    {
        long id = await CreateNodeAsync(name: "OversizeTextNode");
        int cap = Backend.Models.Nodes.InlineContentEncoder.MaxInlineBytes;
        byte[] bytes = Encoding.ASCII.GetBytes(new string('A', cap + 1024));
        await UploadBytesAsync(id, "text/plain", bytes);

        HttpResponseMessage resp = await ListRawAsync($"?id={id}&fields=id,content");
        List<NodeDetails> items = await ReadPageAsync(resp);

        NodeDetails node = items.FirstOrDefault(n => n.Id == id)!;
        Assert.That(node, Is.Not.Null);
        Assert.That(node.Content, Is.Not.Null);
        Assert.That(Encoding.UTF8.GetByteCount(node.Content!), Is.LessThanOrEqualTo(cap),
            "encoded text must not exceed cap bytes");
        Assert.That(node.ContentTruncated, Is.True, "ContentTruncated must be true");
        Assert.That(node.ContentLength, Is.EqualTo(bytes.Length), "ContentLength must reflect original byte count");
    }

    [Test, Parallelizable]
    public async Task List_WithContentField_OversizeBinaryNode_TruncatesBytesBeforeBase64()
    {
        long id = await CreateNodeAsync(name: "OversizeBinaryNode");
        int cap = Backend.Models.Nodes.InlineContentEncoder.MaxInlineBytes;
        byte[] bytes = new byte[cap + 1024];
        new Random(42).NextBytes(bytes);
        await UploadBytesAsync(id, "application/octet-stream", bytes);

        HttpResponseMessage resp = await ListRawAsync($"?id={id}&fields=id,content");
        List<NodeDetails> items = await ReadPageAsync(resp);

        NodeDetails node = items.FirstOrDefault(n => n.Id == id)!;
        Assert.That(node, Is.Not.Null);
        Assert.That(node.Content, Is.Not.Null);
        byte[] decoded = Convert.FromBase64String(node.Content!);
        Assert.That(decoded.Length, Is.EqualTo(cap),
            "base64 must decode to exactly MaxInlineBytes of the original byte stream");
        Assert.That(decoded, Is.EqualTo(bytes[..cap]),
            "decoded bytes must be the leading MaxInlineBytes of the original");
        Assert.That(node.ContentTruncated, Is.True, "ContentTruncated must be true");
        Assert.That(node.ContentLength, Is.EqualTo(bytes.Length), "ContentLength must reflect original byte count");
    }

    [Test, Parallelizable]
    [Description("guards the UTF-8 boundary back-up rule: truncating at MaxInlineBytes must not split a multi-byte code-point")]
    public async Task List_WithContentField_MultibyteBoundary_TruncatesAtCompleteCodepoint()
    {
        long id = await CreateNodeAsync(name: "MultibyteBoundaryNode");
        int cap = Backend.Models.Nodes.InlineContentEncoder.MaxInlineBytes;

        // fill cap-2 bytes with ASCII, then append a 3-byte UTF-8 character (U+4E2D, '中')
        // so the character straddles the cap boundary (1 byte before, 2 bytes after)
        byte[] prefix = Encoding.ASCII.GetBytes(new string('A', cap - 1));
        byte[] cjk = Encoding.UTF8.GetBytes("中");
        byte[] bytes = new byte[prefix.Length + cjk.Length];
        Buffer.BlockCopy(prefix, 0, bytes, 0, prefix.Length);
        Buffer.BlockCopy(cjk, 0, bytes, prefix.Length, cjk.Length);
        await UploadBytesAsync(id, "text/plain", bytes);

        HttpResponseMessage resp = await ListRawAsync($"?id={id}&fields=id,content");
        List<NodeDetails> items = await ReadPageAsync(resp);

        NodeDetails node = items.FirstOrDefault(n => n.Id == id)!;
        Assert.That(node, Is.Not.Null);
        Assert.That(node.Content, Is.Not.Null);
        Assert.That(() => Encoding.UTF8.GetBytes(node.Content!), Throws.Nothing,
            "decoded text must be valid UTF-8 — no replacement characters or exceptions");
        Assert.That(Encoding.UTF8.GetByteCount(node.Content!), Is.LessThanOrEqualTo(cap),
            "encoded bytes must not exceed cap");
        Assert.That(node.ContentTruncated, Is.True, "node exceeds cap — must be truncated");
    }

    [Test, Parallelizable]
    [Description("guards the silent-demotion rule: a text/plain node with invalid UTF-8 bytes must return base64 without throwing")]
    public async Task List_WithContentField_InvalidUtf8InTextTyped_ReturnsBase64()
    {
        long id = await CreateNodeAsync(name: "InvalidUtf8Node");
        // lone 0xFF byte is invalid in UTF-8
        byte[] asciiPart = Encoding.ASCII.GetBytes("valid prefix ");
        byte[] bytes = new byte[asciiPart.Length + 1];
        Buffer.BlockCopy(asciiPart, 0, bytes, 0, asciiPart.Length);
        bytes[asciiPart.Length] = 0xFF;
        await UploadBytesAsync(id, "text/plain", bytes);

        HttpResponseMessage resp = await ListRawAsync($"?id={id}&fields=id,content");
        Assert.That((int) resp.StatusCode, Is.EqualTo(200), "request must succeed even with invalid UTF-8 content");

        List<NodeDetails> items = await ReadPageAsync(resp);
        NodeDetails node = items.FirstOrDefault(n => n.Id == id)!;
        Assert.That(node, Is.Not.Null);
        Assert.That(node.Content, Is.Not.Null, "content must be present");
        byte[] decoded = Convert.FromBase64String(node.Content!);
        Assert.That(decoded, Is.EqualTo(bytes), "silently-demoted content must round-trip as base64");
    }

    [Test, Parallelizable]
    public async Task List_WithContentField_MixedPage_EncodesEachRowCorrectly()
    {
        long textId = await CreateNodeAsync(name: "MixedText");
        long binaryId = await CreateNodeAsync(name: "MixedBinary");
        long emptyId = await CreateNodeAsync(name: "MixedEmpty");

        string textBody = "hello world";
        byte[] binaryBytes = [0x01, 0x02, 0x03, 0x04];
        await UploadTextAsync(textId, "text/plain", textBody);
        await UploadBytesAsync(binaryId, "image/png", binaryBytes);

        HttpResponseMessage resp = await ListRawAsync($"?id={textId},{binaryId},{emptyId}&fields=id,name,content");
        List<NodeDetails> items = await ReadPageAsync(resp);

        NodeDetails textNode = items.FirstOrDefault(n => n.Id == textId)!;
        NodeDetails binaryNode = items.FirstOrDefault(n => n.Id == binaryId)!;
        NodeDetails emptyNode = items.FirstOrDefault(n => n.Id == emptyId)!;

        Assert.That(textNode, Is.Not.Null, "text node must appear");
        Assert.That(textNode.Content, Is.EqualTo(textBody), "text node must return UTF-8 string");

        Assert.That(binaryNode, Is.Not.Null, "binary node must appear");
        Assert.That(Convert.FromBase64String(binaryNode.Content!), Is.EqualTo(binaryBytes),
            "binary node must return base64-encoded bytes");

        Assert.That(emptyNode, Is.Not.Null, "empty node must appear");
        Assert.That(emptyNode.Content, Is.Null, "empty node must have no content field");
    }

    [Test, Parallelizable]
    [Description("load-bearing: sort=content must be rejected (arch doc §10/Q6); commenting out the sort guard must break this test")]
    public async Task List_SortByContent_Returns400()
    {
        HttpResponseMessage resp = await ListRawAsync("?sort=content&count=1");
        Assert.That((int) resp.StatusCode, Is.EqualTo(400), "sort=content must be rejected with HTTP 400");
    }

    [Test, Parallelizable]
    [Description("path-query parity: ?path=...&fields=content must return inline content on terminal-hop rows (arch doc §10/Q7)")]
    public async Task ListByPath_WithContentField_TerminalHopHasInlineContent()
    {
        long projId = await CreateNodeAsync("project", "PathTestProject");
        long docId = await CreateNodeAsync("documentation", "PathTestDoc");

        string docBody = "# path query doc";
        await UploadTextAsync(docId, "text/markdown", docBody);

        using HttpClient client = factory.CreateClient();
        HttpResponseMessage linkResp = await client.PostAsync(
            $"/api/nodes/{projId}/links",
            new StringContent($"{docId}", Encoding.UTF8, "application/json"));
        linkResp.EnsureSuccessStatusCode();

        HttpResponseMessage resp = await ListRawAsync(
            $"?path=[id:{projId}]/[type:documentation]&fields=id,content");
        List<NodeDetails> items = await ReadPageAsync(resp);

        NodeDetails doc = items.FirstOrDefault(n => n.Id == docId)!;
        Assert.That(doc, Is.Not.Null, "documentation node must appear in path query result");
        Assert.That(doc.Content, Is.EqualTo(docBody),
            "path-query terminal hop must carry inline content with identical semantics");
    }

    [Test, Parallelizable]
    [Description("service must auto-include contentType when content is in ?fields= even if not explicitly requested (arch doc §8)")]
    public async Task List_ContentWithoutExplicitContentType_AutoIncludesContentType()
    {
        long id = await CreateNodeAsync(name: "AutoIncludeContentTypeNode");
        await UploadTextAsync(id, "text/markdown", "# test");

        HttpResponseMessage resp = await ListRawAsync($"?id={id}&fields=id,content");
        List<NodeDetails> items = await ReadPageAsync(resp);

        NodeDetails node = items.FirstOrDefault(n => n.Id == id)!;
        Assert.That(node, Is.Not.Null);
        Assert.That(node.ContentType, Is.EqualTo("text/markdown"),
            "contentType must be auto-included when content is in ?fields= but contentType is not");
    }

    [Test, Parallelizable]
    [Description("load-bearing negative test (DiVoid #275): Node.Content bytes must NOT be fetched when content is not in ?fields=. " +
                 "Seeds 5 nodes with 1 MiB each, then asserts the default-fields response is < 50 KiB total.")]
    public async Task List_DefaultFields_LargeContentNodes_ResponseIsSmall()
    {
        byte[] oneMib = new byte[1024 * 1024];
        new Random(7).NextBytes(oneMib);

        long[] ids = new long[5];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = await CreateNodeAsync(name: $"LargeContentNode{i}");
            await UploadBytesAsync(ids[i], "application/octet-stream", oneMib);
        }

        string idList = string.Join(",", ids);
        HttpResponseMessage resp = await ListRawAsync($"?id={idList}&count=5");
        resp.EnsureSuccessStatusCode();
        byte[] responseBytes = await resp.Content.ReadAsByteArrayAsync();

        Assert.That(responseBytes.Length, Is.LessThan(50 * 1024),
            "default-fields listing of 5 x 1 MiB nodes must be < 50 KiB (proves Node.Content bytes are not projected)");
    }

    [Test, Parallelizable]
    public async Task List_OnlyContentField_Works()
    {
        long id = await CreateNodeAsync(name: "OnlyContentFieldNode");
        string body = "content only";
        await UploadTextAsync(id, "text/plain", body);

        HttpResponseMessage resp = await ListRawAsync($"?id={id}&fields=id,content");
        List<NodeDetails> items = await ReadPageAsync(resp);

        NodeDetails node = items.FirstOrDefault(n => n.Id == id)!;
        Assert.That(node, Is.Not.Null);
        Assert.That(node.Content, Is.EqualTo(body), "fields=content alone must return the content inline");
    }

    [Test, Parallelizable]
    public async Task List_TextMarkdownContent_ReturnedAsString()
    {
        long id = await CreateNodeAsync(name: "TextMarkdownBoundary");
        await UploadTextAsync(id, "text/markdown", "# boundary");

        HttpResponseMessage resp = await ListRawAsync($"?id={id}&fields=id,content");
        List<NodeDetails> items = await ReadPageAsync(resp);

        NodeDetails node = items.FirstOrDefault(n => n.Id == id)!;
        Assert.That(node.Content, Is.EqualTo("# boundary"), "text/markdown must be returned as string");
    }

    [Test, Parallelizable]
    public async Task List_ImagePngContent_ReturnedAsBase64()
    {
        long id = await CreateNodeAsync(name: "ImagePngBoundary");
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47];
        await UploadBytesAsync(id, "image/png", bytes);

        HttpResponseMessage resp = await ListRawAsync($"?id={id}&fields=id,content");
        List<NodeDetails> items = await ReadPageAsync(resp);

        NodeDetails node = items.FirstOrDefault(n => n.Id == id)!;
        Assert.That(node.Content, Is.Not.Null);
        byte[] decoded = Convert.FromBase64String(node.Content!);
        Assert.That(decoded, Is.EqualTo(bytes), "image/png must be returned as base64");
    }

    [Test, Parallelizable]
    public async Task List_UnknownContentType_ReturnedAsBase64()
    {
        long id = await CreateNodeAsync(name: "UnknownContentTypeBoundary");
        byte[] bytes = [0x01, 0x02, 0x03];
        await UploadBytesAsync(id, "application/octet-stream", bytes);

        HttpResponseMessage resp = await ListRawAsync($"?id={id}&fields=id,content");
        List<NodeDetails> items = await ReadPageAsync(resp);

        NodeDetails node = items.FirstOrDefault(n => n.Id == id)!;
        Assert.That(node.Content, Is.Not.Null);
        byte[] decoded = Convert.FromBase64String(node.Content!);
        Assert.That(decoded, Is.EqualTo(bytes), "unknown/binary type must be returned as base64");
    }

    [Test, Parallelizable]
    [Description("load-bearing: requesting content once must not permanently widen DefaultListFields for subsequent calls on the same mapper")]
    public async Task List_ContentField_DoesNotWidenDefaultListFields()
    {
        long id = await CreateNodeAsync(name: "DefaultsNotWidenedNode");
        await UploadTextAsync(id, "text/plain", "body");

        await ListRawAsync($"?id={id}&fields=id,content");

        HttpResponseMessage resp = await ListRawAsync($"?id={id}");
        (List<NodeDetails> items, string rawJson) = await ReadPageWithRawAsync(resp);

        NodeDetails node = items.FirstOrDefault(n => n.Id == id)!;
        Assert.That(node, Is.Not.Null);
        Assert.That(node.Content, Is.Null,
            "a prior ?fields=content request must not pollute subsequent default-fields requests");
        Assert.That(rawJson.Contains("\"contentTruncated\""), Is.False);
    }
}
