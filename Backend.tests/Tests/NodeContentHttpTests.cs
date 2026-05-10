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
/// HTTP-layer integration tests for GET /api/nodes/{id}/content.
///
/// Covers:
///   - Bug #175: node exists but has no content → must return 404 (data_entitynotfound),
///     not 500 (unhandled ArgumentNullException).
///   - Happy path: content uploaded → GET returns 200 with the correct bytes.
/// </summary>
[TestFixture]
public class NodeContentHttpTests
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

    async Task<long> CreateNodeAsync(string type = "task", string name = "ContentTestNode")
    {
        NodeDetails created = await http.Post<NodeDetails, NodeDetails>(
            $"{TestSetup.BaseUrl}/api/nodes",
            new NodeDetails { Type = type, Name = name },
            new HttpOptions());
        return created.Id;
    }

    Task<HttpResponseMessage> GetContentAsync(long nodeId)
        => http.Get<HttpResponseMessage>($"{TestSetup.BaseUrl}/api/nodes/{nodeId}/content");

    // -----------------------------------------------------------------------
    // Bug #175 — node with no content must return 404, not 500
    // -----------------------------------------------------------------------

    [Test]
    public async Task GetContent_NodeExistsWithNoContent_Returns404()
    {
        long id = await CreateNodeAsync(name: "NoContentNode");

        HttpResponseMessage resp = await GetContentAsync(id);

        Assert.That((int) resp.StatusCode, Is.EqualTo(404),
            "GET /api/nodes/{id}/content for a content-less node must return 404, not 500 (bug #175)");
    }

    [Test]
    public async Task GetContent_NodeExistsWithNoContent_ReturnsEntityNotFoundCode()
    {
        long id = await CreateNodeAsync(name: "NoContentNodeCode");

        HttpResponseMessage resp = await GetContentAsync(id);
        string body = await resp.Content.ReadAsStringAsync();
        ErrorResponse error = Json.Read<ErrorResponse>(body);

        Assert.That(error.Code, Is.EqualTo("data_entitynotfound"),
            "error code must be data_entitynotfound for content-less node (bug #175)");
    }

    // -----------------------------------------------------------------------
    // Positive path — upload content then retrieve it
    // -----------------------------------------------------------------------

    [Test]
    public async Task GetContent_AfterUpload_Returns200WithCorrectBytes()
    {
        long id = await CreateNodeAsync(name: "ContentUploadNode");
        byte[] payload = Encoding.UTF8.GetBytes("hello content");

        using HttpClient client = factory.CreateClient();
        HttpContent uploadBody = new ByteArrayContent(payload);
        uploadBody.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        HttpResponseMessage uploadResp = await client.PostAsync($"/api/nodes/{id}/content", uploadBody);
        Assert.That((int) uploadResp.StatusCode, Is.EqualTo(200),
            "content upload must return 200");

        HttpResponseMessage getResp = await GetContentAsync(id);
        Assert.That((int) getResp.StatusCode, Is.EqualTo(200),
            "GET content after upload must return 200");

        byte[] returned = await getResp.Content.ReadAsByteArrayAsync();
        Assert.That(returned, Is.EqualTo(payload),
            "returned bytes must match what was uploaded");
    }
}
