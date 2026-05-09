using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Pooshit.Http;
using Pooshit.Json;

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

    Task<NodeDetails> PostNodeAsync(NodeDetails payload)
        => http.Post<NodeDetails, NodeDetails>(
            $"{TestSetup.BaseUrl}/api/nodes",
            payload,
            new HttpOptions());

    Task<NodeDetails> GetNodeAsync(long id)
        => http.Get<NodeDetails>(
            $"{TestSetup.BaseUrl}/api/nodes/{id}",
            new HttpOptions());

    // -----------------------------------------------------------------------
    // Bug #157 — status must survive create → GET round-trip
    // -----------------------------------------------------------------------

    [Test]
    public async Task CreateNode_WithStatus_StatusPersistedToDatabase()
    {
        // POST with status set
        NodeDetails created = await PostNodeAsync(new NodeDetails { Type = "task", Name = "StatusRoundTrip", Status = "open" });
        Assert.That(created.Id, Is.GreaterThan(0), "POST must return a valid id");

        // Re-read from a fresh GET — confirms DB row was written, not just DTO echoed
        NodeDetails fetched = await GetNodeAsync(created.Id);

        Assert.That(fetched.Status, Is.EqualTo("open"),
            "status set on POST /api/nodes must survive a subsequent GET (bug #157)");
    }

    [Test]
    public async Task CreateNode_WithStatus_CreateResponseAlsoReflectsStatus()
    {
        // The POST response itself must carry the persisted value (re-read path)
        NodeDetails created = await PostNodeAsync(new NodeDetails { Type = "task", Name = "StatusInResponse", Status = "in-progress" });

        Assert.That(created.Status, Is.EqualTo("in-progress"),
            "POST /api/nodes response must reflect the persisted status, not merely echo the input");
    }

    [Test]
    public async Task CreateNode_WithoutStatus_StatusIsNullAfterGet()
    {
        // Nodes created without a status must not default to a non-null value
        NodeDetails created = await PostNodeAsync(new NodeDetails { Type = "task", Name = "NoStatusNode" });

        NodeDetails fetched = await GetNodeAsync(created.Id);

        Assert.That(fetched.Status, Is.Null.Or.Empty,
            "nodes created without a status must have null/empty status after GET");
    }
}
