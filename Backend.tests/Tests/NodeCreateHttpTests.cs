using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// HTTP-layer integration tests for the POST /api/nodes endpoint.
///
/// These tests verify that fields supplied on the create body are actually persisted
/// to the database and survive a subsequent GET — not merely echoed from the inbound DTO.
///
/// Auth is disabled for simplicity — persistence behaviour is orthogonal to auth.
/// </summary>
[TestFixture]
public class NodeCreateHttpTests
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
                    ["Database:Source"] = $"/tmp/divoid_create_test_{Guid.NewGuid():N}.db3"
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

    static StringContent JsonBody(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    async Task<JsonDocument> PostNodeAsync(object payload)
    {
        HttpResponseMessage resp = await _client.PostAsync("/api/nodes", JsonBody(payload));
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"POST /api/nodes failed {(int) resp.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    async Task<JsonDocument> GetNodeAsync(long id)
    {
        HttpResponseMessage resp = await _client.GetAsync($"/api/nodes/{id}");
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"GET /api/nodes/{id} failed {(int) resp.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    // -----------------------------------------------------------------------
    // Bug #157 — status must survive create → GET round-trip
    // -----------------------------------------------------------------------

    [Test]
    public async Task CreateNode_WithStatus_StatusPersistedToDatabase()
    {
        // POST with status set
        using JsonDocument createDoc = await PostNodeAsync(new { type = "task", name = "StatusRoundTrip", status = "open" });
        long id = createDoc.RootElement.GetProperty("id").GetInt64();
        Assert.That(id, Is.GreaterThan(0), "POST must return a valid id");

        // Re-read from a fresh GET — confirms DB row was written, not just DTO echoed
        using JsonDocument getDoc = await GetNodeAsync(id);
        string? status = getDoc.RootElement.GetProperty("status").GetString();

        Assert.That(status, Is.EqualTo("open"),
            "status set on POST /api/nodes must survive a subsequent GET (bug #157)");
    }

    [Test]
    public async Task CreateNode_WithStatus_CreateResponseAlsoReflectsStatus()
    {
        // The POST response itself must carry the persisted value (re-read path)
        using JsonDocument createDoc = await PostNodeAsync(new { type = "task", name = "StatusInResponse", status = "in-progress" });

        string? status = createDoc.RootElement.GetProperty("status").GetString();

        Assert.That(status, Is.EqualTo("in-progress"),
            "POST /api/nodes response must reflect the persisted status, not merely echo the input");
    }

    [Test]
    public async Task CreateNode_WithoutStatus_StatusIsNullAfterGet()
    {
        // Nodes created without a status must not default to a non-null value
        using JsonDocument createDoc = await PostNodeAsync(new { type = "task", name = "NoStatusNode" });
        long id = createDoc.RootElement.GetProperty("id").GetInt64();

        using JsonDocument getDoc = await GetNodeAsync(id);
        // status may be absent or explicitly null
        bool hasStatus = getDoc.RootElement.TryGetProperty("status", out JsonElement statusEl);
        string? status = hasStatus ? statusEl.GetString() : null;

        Assert.That(status, Is.Null.Or.Empty,
            "nodes created without a status must have null/empty status after GET");
    }
}
