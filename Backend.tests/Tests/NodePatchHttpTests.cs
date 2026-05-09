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

    async Task<long> CreateNodeAsync(string type = "task", string name = "TestNode")
    {
        NodeDetails created = await http.Post<NodeDetails, NodeDetails>(
            $"{TestSetup.BaseUrl}/api/nodes",
            new NodeDetails { Type = type, Name = name },
            new HttpOptions());
        return created.Id;
    }

    Task<HttpResponseMessage> PatchAsync(string url, PatchOperation[] ops)
        => http.Patch<PatchOperation[], HttpResponseMessage>(url, ops);

    // -----------------------------------------------------------------------
    // 200 happy paths must keep working
    // -----------------------------------------------------------------------

    [Test]
    public async Task Patch_ValidPath_Name_Returns200()
    {
        long id = await CreateNodeAsync(name: "OriginalName");
        PatchOperation[] ops = [new() { Op = "replace", Path = "/name", Value = "UpdatedName" }];
        HttpResponseMessage resp = await PatchAsync($"{TestSetup.BaseUrl}/api/nodes/{id}", ops);
        Assert.That((int) resp.StatusCode, Is.EqualTo(200));
    }

    [Test]
    public async Task Patch_ValidPath_Status_Returns200()
    {
        long id = await CreateNodeAsync();
        PatchOperation[] ops = [new() { Op = "replace", Path = "/status", Value = "open" }];
        HttpResponseMessage resp = await PatchAsync($"{TestSetup.BaseUrl}/api/nodes/{id}", ops);
        Assert.That((int) resp.StatusCode, Is.EqualTo(200));
    }

    // -----------------------------------------------------------------------
    // 404 — node does not exist
    // -----------------------------------------------------------------------

    [Test]
    public async Task Patch_NonExistentNode_Returns404()
    {
        PatchOperation[] ops = [new() { Op = "replace", Path = "/status", Value = "open" }];
        HttpResponseMessage resp = await PatchAsync($"{TestSetup.BaseUrl}/api/nodes/999999", ops);
        Assert.That((int) resp.StatusCode, Is.EqualTo(404));
    }

    // -----------------------------------------------------------------------
    // 400 — property exists but is not [AllowPatch]
    // -----------------------------------------------------------------------

    [Test]
    public async Task Patch_NonAllowPatchedProperty_TypeId_Returns400()
    {
        long id = await CreateNodeAsync();
        PatchOperation[] ops = [new() { Op = "replace", Path = "/typeid", Value = 99L }];
        HttpResponseMessage resp = await PatchAsync($"{TestSetup.BaseUrl}/api/nodes/{id}", ops);
        Assert.That((int) resp.StatusCode, Is.EqualTo(400),
            "Patching a property that exists but lacks [AllowPatch] must return 400, not 500");
    }

    [Test]
    public async Task Patch_NonAllowPatchedProperty_TypeId_ReturnsErrorBody()
    {
        long id = await CreateNodeAsync();
        PatchOperation[] ops = [new() { Op = "replace", Path = "/typeid", Value = 99L }];
        HttpResponseMessage resp = await PatchAsync($"{TestSetup.BaseUrl}/api/nodes/{id}", ops);
        string body = await resp.Content.ReadAsStringAsync();
        ErrorResponse error = Json.Read<ErrorResponse>(body);
        Assert.That(error.Code, Is.EqualTo("badparameter"));
    }

    // -----------------------------------------------------------------------
    // 400 — path does not map to any property
    // -----------------------------------------------------------------------

    [Test]
    public async Task Patch_NonExistentPath_Returns400()
    {
        long id = await CreateNodeAsync();
        PatchOperation[] ops = [new() { Op = "replace", Path = "/banana", Value = "yes" }];
        HttpResponseMessage resp = await PatchAsync($"{TestSetup.BaseUrl}/api/nodes/{id}", ops);
        Assert.That((int) resp.StatusCode, Is.EqualTo(400),
            "Patching a path that resolves to no property must return 400, not 500 or 404");
    }

    [Test]
    public async Task Patch_NonExistentPath_ReturnsErrorBody()
    {
        long id = await CreateNodeAsync();
        PatchOperation[] ops = [new() { Op = "replace", Path = "/banana", Value = "yes" }];
        HttpResponseMessage resp = await PatchAsync($"{TestSetup.BaseUrl}/api/nodes/{id}", ops);
        string body = await resp.Content.ReadAsStringAsync();
        ErrorResponse error = Json.Read<ErrorResponse>(body);
        Assert.That(error.Code, Is.EqualTo("badparameter"));
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
        PatchOperation[] ops = [new() { Op = "replace", Path = "/type", Value = "other" }];
        HttpResponseMessage resp = await PatchAsync($"{TestSetup.BaseUrl}/api/nodes/{id}", ops);
        Assert.That((int) resp.StatusCode, Is.EqualTo(400));
    }
}
