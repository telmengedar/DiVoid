using System.Net.Http;
using Backend.Models.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Errors;
using Pooshit.Http;
using Pooshit.Json;

namespace Backend.tests.Tests;

/// <summary>
/// HTTP-layer integration test for POST /api/nodes/{sourceNodeId}/links with a self-link.
/// </summary>
[TestFixture]
public class NodeLinkSelfLinkHttpTests
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

    async Task<long> CreateNodeAsync(string name)
    {
        NodeDetails created = await http.Post<NodeDetails, NodeDetails>(
            $"{TestSetup.BaseUrl}/api/nodes",
            new NodeDetails { Type = "task", Name = name },
            new HttpOptions());
        return created.Id;
    }

    Task<HttpResponseMessage> LinkAsync(long sourceId, long targetId)
        => http.Post<long, HttpResponseMessage>($"{TestSetup.BaseUrl}/api/nodes/{sourceId}/links", targetId);

    [Test, Parallelizable]
    [Description("DiVoid #1183 — a self-link is a caller error and must return 400, not 500.")]
    public async Task PostLink_SelfLink_Returns400WithBadParameterCode()
    {
        long nodeId = await CreateNodeAsync("SelfLink_A1");

        HttpResponseMessage response = await LinkAsync(nodeId, nodeId);

        Assert.That((int) response.StatusCode, Is.EqualTo(400),
            "linking a node to itself must return 400 not 500");

        ErrorResponse error = Json.Read<ErrorResponse>(await response.Content.ReadAsStringAsync());
        Assert.That(error.Code, Is.EqualTo("badparameter"),
            "error code must be badparameter");
    }
}
