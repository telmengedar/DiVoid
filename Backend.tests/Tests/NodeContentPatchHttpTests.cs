using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Errors;
using Pooshit.Http;
using Pooshit.Json;

namespace Backend.tests.Tests;

/// <summary>
/// HTTP-layer integration tests for PATCH /api/nodes/{id}/content.
///
/// verifies the end-to-end wire contract an MCP client uses: JSON edit list in, edited bytes
/// persisted, and the anti-corruption faults surfaced as 400 / 404 rather than 500.
/// </summary>
[TestFixture]
public class NodeContentPatchHttpTests
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

    async Task<long> CreateNodeAsync(string name = "PatchContentNode")
    {
        NodeDetails created = await http.Post<NodeDetails, NodeDetails>(
            $"{TestSetup.BaseUrl}/api/nodes",
            new NodeDetails { Type = "task", Name = name },
            new HttpOptions());
        return created.Id;
    }

    async Task UploadAsync(long nodeId, string content, string contentType = "text/plain")
    {
        using HttpClient client = factory.CreateClient();
        HttpContent body = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        HttpResponseMessage resp = await client.PostAsync($"/api/nodes/{nodeId}/content", body);
        Assert.That((int) resp.StatusCode, Is.EqualTo(200), "precondition: content upload must succeed");
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
        return await resp.Content.ReadAsStringAsync();
    }

    [Test]
    public async Task PatchContent_ReplaceLine_PersistsEditedContent()
    {
        long id = await CreateNodeAsync();
        await UploadAsync(id, "alpha\nbravo\ncharlie\n");

        HttpResponseMessage resp = await PatchContentAsync(id,
            """[ { "unit": "line", "start": 1, "length": 1, "value": "BRAVO\n" } ]""");

        Assert.That((int) resp.StatusCode, Is.EqualTo(200));
        Assert.That(await GetContentStringAsync(id), Is.EqualTo("alpha\nBRAVO\ncharlie\n"));
    }

    [Test]
    public async Task PatchContent_ReplaceCharRange_PersistsEditedContent()
    {
        long id = await CreateNodeAsync();
        await UploadAsync(id, "hello world");

        HttpResponseMessage resp = await PatchContentAsync(id,
            """[ { "unit": "char", "start": 0, "length": 5, "value": "HELLO" } ]""");

        Assert.That((int) resp.StatusCode, Is.EqualTo(200));
        Assert.That(await GetContentStringAsync(id), Is.EqualTo("HELLO world"));
    }

    [Test]
    public async Task PatchContent_MultipleEdits_AppliedAgainstOriginalFrame()
    {
        long id = await CreateNodeAsync();
        await UploadAsync(id, "0123456789");

        HttpResponseMessage resp = await PatchContentAsync(id,
            """[ { "unit": "char", "start": 0, "length": 2, "value": "AA" }, { "unit": "char", "start": 5, "length": 2, "value": "BB" } ]""");

        Assert.That((int) resp.StatusCode, Is.EqualTo(200));
        Assert.That(await GetContentStringAsync(id), Is.EqualTo("AA234BB789"));
    }

    [Test]
    public async Task PatchContent_NonExistentNode_Returns404()
    {
        HttpResponseMessage resp = await PatchContentAsync(999999,
            """[ { "unit": "char", "start": 0, "length": 1, "value": "x" } ]""");
        Assert.That((int) resp.StatusCode, Is.EqualTo(404));
    }

    [Test]
    public async Task PatchContent_NodeWithNoContent_Returns404()
    {
        long id = await CreateNodeAsync(name: "NoContentForPatch");
        HttpResponseMessage resp = await PatchContentAsync(id,
            """[ { "unit": "char", "start": 0, "length": 0, "value": "x" } ]""");
        Assert.That((int) resp.StatusCode, Is.EqualTo(404));
    }

    [Test]
    public async Task PatchContent_OutOfRange_Returns400AndLeavesContentUntouched()
    {
        long id = await CreateNodeAsync();
        await UploadAsync(id, "abc");

        HttpResponseMessage resp = await PatchContentAsync(id,
            """[ { "unit": "char", "start": 2, "length": 99, "value": "x" } ]""");

        Assert.That((int) resp.StatusCode, Is.EqualTo(400));
        string body = await resp.Content.ReadAsStringAsync();
        ErrorResponse error = Json.Read<ErrorResponse>(body);
        Assert.That(error.Code, Is.EqualTo("badparameter"));
        Assert.That(await GetContentStringAsync(id), Is.EqualTo("abc"),
            "a rejected edit must not mutate the stored content");
    }

    [Test]
    public async Task PatchContent_NonTextContent_Returns400()
    {
        long id = await CreateNodeAsync();
        await UploadAsync(id, "binaryish", "application/octet-stream");

        HttpResponseMessage resp = await PatchContentAsync(id,
            """[ { "unit": "char", "start": 0, "length": 1, "value": "x" } ]""");

        Assert.That((int) resp.StatusCode, Is.EqualTo(400));
    }

    [Test]
    public async Task PatchContent_OverlappingEdits_Returns400()
    {
        long id = await CreateNodeAsync();
        await UploadAsync(id, "abcdefgh");

        HttpResponseMessage resp = await PatchContentAsync(id,
            """[ { "unit": "char", "start": 0, "length": 5, "value": "x" }, { "unit": "char", "start": 3, "length": 3, "value": "y" } ]""");

        Assert.That((int) resp.StatusCode, Is.EqualTo(400));
    }
}
