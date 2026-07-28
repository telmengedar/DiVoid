using System.Net.Http;
using Backend.Models.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Errors;
using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Http;
using Pooshit.Json;

namespace Backend.tests.Tests;

/// <summary>
/// HTTP-layer integration tests for <c>PATCH /api/nodes/{sourceNodeId}/links/{targetNodeId}</c>
/// (DiVoid #7201) — editing an existing edge's <c>linkType</c>/<c>context</c> in place.
///
/// Covers the acceptance matrix:
///   - 200 happy paths for linkType-only, context-only, and both together.
///   - 404 when no edge exists between the two nodes.
///   - 400 when the patch targets a property that is not <c>[AllowPatch]</c> (<c>sourceId</c>).
///   - Wire-shape (raw JSON): camelCase keys, linkType serialized as its string enum name.
///
/// Auth is disabled — the error-mapping/wire-shape logic under test is orthogonal to auth.
/// </summary>
[TestFixture]
public class NodeLinkPatchHttpTests
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

    async Task<long> CreateNodeAsync(string type = "task", string name = "LinkPatchTestNode")
    {
        NodeDetails created = await http.Post<NodeDetails, NodeDetails>(
            $"{TestSetup.BaseUrl}/api/nodes",
            new NodeDetails { Type = type, Name = name },
            new HttpOptions());
        return created.Id;
    }

    Task<HttpResponseMessage> LinkAsync(long sourceId, long targetId, string query = "")
        => http.Post<long, HttpResponseMessage>($"{TestSetup.BaseUrl}/api/nodes/{sourceId}/links{query}", targetId);

    Task<HttpResponseMessage> PatchLinkAsync(long sourceId, long targetId, PatchOperation[] ops)
        => http.Patch<PatchOperation[], HttpResponseMessage>($"{TestSetup.BaseUrl}/api/nodes/{sourceId}/links/{targetId}", ops);

    [Test, Parallelizable]
    public async Task PatchLink_LinkTypeOnly_Returns200AndPersistsValue()
    {
        long source = await CreateNodeAsync(name: "LinkTypeOnlySource");
        long target = await CreateNodeAsync(name: "LinkTypeOnlyTarget");
        (await LinkAsync(source, target)).EnsureSuccessStatusCode();

        PatchOperation[] ops = [new() { Op = "replace", Path = "/linkType", Value = (int) LinkType.Unidirectional }];
        HttpResponseMessage resp = await PatchLinkAsync(source, target, ops);
        Assert.That((int) resp.StatusCode, Is.EqualTo(200),
            "PATCH replace /linkType must succeed because LinkType is [AllowPatch]");

        NodeLink patched = Json.Read<NodeLink>(await resp.Content.ReadAsStringAsync())!;
        Assert.That(patched.LinkType, Is.EqualTo(LinkType.Unidirectional));
    }

    [Test, Parallelizable]
    public async Task PatchLink_ContextOnly_Returns200AndPersistsValue()
    {
        long source = await CreateNodeAsync(name: "ContextOnlySource");
        long target = await CreateNodeAsync(name: "ContextOnlyTarget");
        (await LinkAsync(source, target)).EnsureSuccessStatusCode();

        PatchOperation[] ops = [new() { Op = "replace", Path = "/context", Value = "subtask" }];
        HttpResponseMessage resp = await PatchLinkAsync(source, target, ops);
        Assert.That((int) resp.StatusCode, Is.EqualTo(200),
            "PATCH replace /context must succeed because Context is [AllowPatch]");

        NodeLink patched = Json.Read<NodeLink>(await resp.Content.ReadAsStringAsync())!;
        Assert.That(patched.Context, Is.EqualTo("subtask"));
    }

    [Test, Parallelizable]
    public async Task PatchLink_LinkTypeAndContext_Returns200AndPersistsBoth()
    {
        long source = await CreateNodeAsync(name: "BothFieldsSource");
        long target = await CreateNodeAsync(name: "BothFieldsTarget");
        (await LinkAsync(source, target)).EnsureSuccessStatusCode();

        PatchOperation[] ops = [
            new() { Op = "replace", Path = "/linkType", Value = (int) LinkType.Bidirectional },
            new() { Op = "replace", Path = "/context", Value = "blocks" }
        ];
        HttpResponseMessage resp = await PatchLinkAsync(source, target, ops);
        Assert.That((int) resp.StatusCode, Is.EqualTo(200));

        NodeLink patched = Json.Read<NodeLink>(await resp.Content.ReadAsStringAsync())!;
        Assert.Multiple(() => {
            Assert.That(patched.LinkType, Is.EqualTo(LinkType.Bidirectional));
            Assert.That(patched.Context, Is.EqualTo("blocks"));
        });
    }

    [Test, Parallelizable]
    public async Task PatchLink_ReverseAddressedEdge_Returns200WithStoredOrientation()
    {
        long source = await CreateNodeAsync(name: "ReverseAddrSource");
        long target = await CreateNodeAsync(name: "ReverseAddrTarget");
        (await LinkAsync(source, target)).EnsureSuccessStatusCode();

        PatchOperation[] ops = [new() { Op = "replace", Path = "/context", Value = "reverse-addressed" }];
        HttpResponseMessage resp = await PatchLinkAsync(target, source, ops);
        Assert.That((int) resp.StatusCode, Is.EqualTo(200),
            "the edge must be addressable in either orientation, mirroring the DELETE link route");

        NodeLink patched = Json.Read<NodeLink>(await resp.Content.ReadAsStringAsync())!;
        Assert.Multiple(() => {
            Assert.That(patched.SourceId, Is.EqualTo(source), "response must reflect the actual stored source→target orientation, not the caller's addressing order");
            Assert.That(patched.TargetId, Is.EqualTo(target));
        });
    }

    [Test, Parallelizable]
    public async Task PatchLink_NoEdgeBetweenNodes_Returns404()
    {
        long source = await CreateNodeAsync(name: "NoEdgeSource");
        long target = await CreateNodeAsync(name: "NoEdgeTarget");

        PatchOperation[] ops = [new() { Op = "replace", Path = "/context", Value = "no-edge" }];
        HttpResponseMessage resp = await PatchLinkAsync(source, target, ops);
        Assert.That((int) resp.StatusCode, Is.EqualTo(404),
            "patching a link that does not exist must return 404, not 500");
    }

    [Test, Parallelizable]
    public async Task PatchLink_NonAllowPatchedProperty_SourceId_Returns400()
    {
        long source = await CreateNodeAsync(name: "NonAllowPatchSource");
        long target = await CreateNodeAsync(name: "NonAllowPatchTarget");
        (await LinkAsync(source, target)).EnsureSuccessStatusCode();

        PatchOperation[] ops = [new() { Op = "replace", Path = "/sourceId", Value = 99999L }];
        HttpResponseMessage resp = await PatchLinkAsync(source, target, ops);
        Assert.That((int) resp.StatusCode, Is.EqualTo(400),
            "patching sourceId must return 400 — it is edge identity, not an [AllowPatch] property");

        string body = await resp.Content.ReadAsStringAsync();
        ErrorResponse error = Json.Read<ErrorResponse>(body);
        Assert.That(error.Code, Is.EqualTo("badparameter"));
    }

    [Test, Parallelizable]
    [Description("wire-shape: raw JSON uses camelCase keys and serializes linkType as its string enum name (DiVoid #7201 §6.6)")]
    public async Task PatchLink_RawJson_WireShapeIsCamelCaseWithStringEnum()
    {
        long source = await CreateNodeAsync(name: "WireShapeSource");
        long target = await CreateNodeAsync(name: "WireShapeTarget");
        (await LinkAsync(source, target)).EnsureSuccessStatusCode();

        PatchOperation[] ops = [
            new() { Op = "replace", Path = "/linkType", Value = (int) LinkType.Bidirectional },
            new() { Op = "replace", Path = "/context", Value = "wireshape" }
        ];
        HttpResponseMessage resp = await PatchLinkAsync(source, target, ops);
        string rawJson = await resp.Content.ReadAsStringAsync();

        Assert.Multiple(() => {
            Assert.That(rawJson.Contains("\"linkType\":\"Bidirectional\""), Is.True, "linkType must serialize as its string enum name, not a numeric value");
            Assert.That(rawJson.Contains("\"context\":\"wireshape\""), Is.True, "context must serialize as a plain string");
            Assert.That(rawJson.Contains($"\"sourceId\":{source}"), Is.True, "sourceId must serialize as a camelCase key");
            Assert.That(rawJson.Contains($"\"targetId\":{target}"), Is.True, "targetId must serialize as a camelCase key");
        });
    }
}
