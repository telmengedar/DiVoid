using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// HTTP-layer integration tests for the PATCH /api/nodes/{id} endpoint.
///
/// These tests verify that client-input errors (bad patch paths) produce HTTP 400 with
/// the standard error-response body, rather than falling through to HTTP 500.
///
/// Auth is disabled for simplicity — the error-mapping logic is orthogonal to auth.
/// </summary>
[TestFixture]
public class NodePatchHttpTests
{
    WebApplicationFactory<Program> _factory = null!;
    HttpClient _client = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => {
            builder.ConfigureAppConfiguration((_, config) => {
                config.AddInMemoryCollection(new Dictionary<string, string?> {
                    ["Auth:Enabled"] = "false",
                    ["Database:Type"] = "Sqlite",
                    ["Database:Source"] = $"/tmp/divoid_http_test_{Guid.NewGuid():N}.db3"
                });
            });
        });
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    static StringContent PatchBody(params (string op, string path, object value)[] ops)
    {
        var operations = Array.ConvertAll(ops, o => new { op = o.op, path = o.path, value = o.value });
        return new StringContent(JsonSerializer.Serialize(operations), Encoding.UTF8, "application/json");
    }

    async Task<long> CreateNodeAsync(string type = "task", string name = "TestNode")
    {
        var body = new StringContent(
            JsonSerializer.Serialize(new { type, name }),
            Encoding.UTF8,
            "application/json");
        HttpResponseMessage resp = await _client.PostAsync("/api/nodes", body);
        if (!resp.IsSuccessStatusCode) {
            string content = await resp.Content.ReadAsStringAsync();
            throw new Exception($"POST /api/nodes failed {(int)resp.StatusCode}: {content}");
        }
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetInt64();
    }

    static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage resp)
    {
        string json = await resp.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json);
    }

    // -----------------------------------------------------------------------
    // 200 happy paths must keep working
    // -----------------------------------------------------------------------

    [Test]
    public async Task Patch_ValidPath_Name_Returns200()
    {
        long id = await CreateNodeAsync(name: "OriginalName");
        HttpResponseMessage resp = await _client.PatchAsync(
            $"/api/nodes/{id}",
            PatchBody(("replace", "/name", "UpdatedName")));
        Assert.That((int) resp.StatusCode, Is.EqualTo(200));
    }

    [Test]
    public async Task Patch_ValidPath_Status_Returns200()
    {
        long id = await CreateNodeAsync();
        HttpResponseMessage resp = await _client.PatchAsync(
            $"/api/nodes/{id}",
            PatchBody(("replace", "/status", "open")));
        Assert.That((int) resp.StatusCode, Is.EqualTo(200));
    }

    // -----------------------------------------------------------------------
    // 404 — node does not exist
    // -----------------------------------------------------------------------

    [Test]
    public async Task Patch_NonExistentNode_Returns404()
    {
        HttpResponseMessage resp = await _client.PatchAsync(
            "/api/nodes/999999",
            PatchBody(("replace", "/status", "open")));
        Assert.That((int) resp.StatusCode, Is.EqualTo(404));
    }

    // -----------------------------------------------------------------------
    // 400 — property exists but is not [AllowPatch]
    // -----------------------------------------------------------------------

    [Test]
    public async Task Patch_NonAllowPatchedProperty_TypeId_Returns400()
    {
        long id = await CreateNodeAsync();
        HttpResponseMessage resp = await _client.PatchAsync(
            $"/api/nodes/{id}",
            PatchBody(("replace", "/typeid", 99)));
        Assert.That((int) resp.StatusCode, Is.EqualTo(400),
            "Patching a property that exists but lacks [AllowPatch] must return 400, not 500");
    }

    [Test]
    public async Task Patch_NonAllowPatchedProperty_TypeId_ReturnsErrorBody()
    {
        long id = await CreateNodeAsync();
        HttpResponseMessage resp = await _client.PatchAsync(
            $"/api/nodes/{id}",
            PatchBody(("replace", "/typeid", 99)));
        using JsonDocument doc = await ReadJsonAsync(resp);
        Assert.That(doc.RootElement.TryGetProperty("code", out _), Is.True,
            "Error body must contain 'code' field");
        Assert.That(doc.RootElement.GetProperty("code").GetString(), Is.EqualTo("badparameter"));
    }

    // -----------------------------------------------------------------------
    // 400 — path does not map to any property
    // -----------------------------------------------------------------------

    [Test]
    public async Task Patch_NonExistentPath_Returns400()
    {
        long id = await CreateNodeAsync();
        HttpResponseMessage resp = await _client.PatchAsync(
            $"/api/nodes/{id}",
            PatchBody(("replace", "/banana", "yes")));
        Assert.That((int) resp.StatusCode, Is.EqualTo(400),
            "Patching a path that resolves to no property must return 400, not 500 or 404");
    }

    [Test]
    public async Task Patch_NonExistentPath_ReturnsErrorBody()
    {
        long id = await CreateNodeAsync();
        HttpResponseMessage resp = await _client.PatchAsync(
            $"/api/nodes/{id}",
            PatchBody(("replace", "/banana", "yes")));
        using JsonDocument doc = await ReadJsonAsync(resp);
        Assert.That(doc.RootElement.TryGetProperty("code", out _), Is.True,
            "Error body must contain 'code' field");
        Assert.That(doc.RootElement.GetProperty("code").GetString(), Is.EqualTo("badparameter"));
    }

    // -----------------------------------------------------------------------
    // 400 — /type path (maps to no Node entity property; TypeId is the DB column)
    // -----------------------------------------------------------------------

    [Test]
    public async Task Patch_TypePath_Returns400()
    {
        // "/type" does not map to any property on Node (the entity uses TypeId).
        // This path was specifically called out in the service-layer tests as PropertyNotFoundException.
        long id = await CreateNodeAsync();
        HttpResponseMessage resp = await _client.PatchAsync(
            $"/api/nodes/{id}",
            PatchBody(("replace", "/type", "other")));
        Assert.That((int) resp.StatusCode, Is.EqualTo(400));
    }
}
