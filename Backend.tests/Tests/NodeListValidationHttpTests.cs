using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Pooshit.Http;

namespace Backend.tests.Tests;

/// <summary>
/// HTTP-layer integration tests verifying that unknown <c>?sort=</c> values and unknown
/// <c>?fields=</c> names return HTTP 400 instead of 500, and that <c>?fields=similarity</c>
/// without <c>?query=</c> returns HTTP 400 with a descriptive message.
///
/// Closes DiVoid #176 (unknown sort → 400) and DiVoid #1354 (similarity without query → 400).
/// </summary>
[TestFixture]
public class NodeListValidationHttpTests
{
    WebApplicationFactory<Program> factory = null!;
    HttpClient client = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        factory = TestSetup.CreateTestFactory();
        client = factory.CreateClient();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        client.Dispose();
        factory.Dispose();
    }

    static async Task<JsonDocument> ReadJsonDocumentAsync(HttpResponseMessage response)
    {
        string json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json);
    }

    /// <summary>
    /// Load-bearing check (#275): mentally delete the FilterExtensions sort wrap — this test
    /// fails because the unfixed path throws KeyNotFoundException which falls through to 500.
    /// The assertion on status 400 catches the revert.
    /// </summary>
    [Test]
    public async Task List_UnknownSort_Returns400WithBadParameterCode()
    {
        HttpResponseMessage response = await client.GetAsync("/api/nodes?sort=bogus&count=1");

        Assert.That((int)response.StatusCode, Is.EqualTo(400),
            "unknown sort value must return 400 not 500");

        using JsonDocument doc = await ReadJsonDocumentAsync(response);
        JsonElement root = doc.RootElement;

        Assert.Multiple(() => {
            Assert.That(root.GetProperty("code").GetString(), Is.EqualTo("badparameter"),
                "error code must be badparameter");
            Assert.That(root.GetProperty("text").GetString(), Does.Contain("bogus"),
                "error text must mention the offending sort value");
            JsonElement ctx = root.GetProperty("context");
            string[] available = ctx.GetProperty("available").EnumerateArray()
                                    .Select(e => e.GetString()!)
                                    .ToArray();
            Assert.That(available, Is.Not.Empty,
                "available array must be non-empty");
        });
    }

    /// <summary>
    /// Load-bearing check (#275): mentally delete the UnknownFieldExceptionHandler registration
    /// in Startup.cs — this test fails because the exception falls through to the unhandled
    /// handler which returns 500 with code "unhandled". The assertion on status 400 catches the revert.
    /// </summary>
    [Test]
    public async Task List_UnknownField_Returns400WithFieldAndAvailableInBody()
    {
        HttpResponseMessage response = await client.GetAsync("/api/nodes?fields=id,name,bogus&count=1");

        Assert.That((int)response.StatusCode, Is.EqualTo(400),
            "unknown field name must return 400 not 500");

        using JsonDocument doc = await ReadJsonDocumentAsync(response);
        JsonElement root = doc.RootElement;

        Assert.Multiple(() => {
            Assert.That(root.GetProperty("code").GetString(), Is.EqualTo("badparameter"),
                "error code must be badparameter");
            JsonElement ctx = root.GetProperty("context");
            Assert.That(ctx.GetProperty("field").GetString(), Is.EqualTo("bogus"),
                "field extra must name the offending field");
            string[] available = ctx.GetProperty("available").EnumerateArray()
                                    .Select(e => e.GetString()!)
                                    .ToArray();
            Assert.That(available, Does.Contain("id"),
                "available must include 'id'");
            Assert.That(available, Does.Contain("name"),
                "available must include 'name'");
        });
    }

    /// <summary>
    /// Load-bearing check (#275): mentally delete the similarity pre-check in NodeService.ListPaged
    /// — the test fails because without the pre-check the mapper does not register 'similarity'
    /// (no Query present), DbFieldsFromNames throws UnknownFieldException, which maps to 400 but
    /// with generic code rather than the specific message. The assertion on the text content
    /// containing "only available" and "?query=" catches the revert: without the pre-check the
    /// message would be "Unknown field 'similarity'. Available: id, ..." not the user-friendly form.
    /// </summary>
    [Test]
    public async Task List_SimilarityWithoutQuery_Returns400WithDescriptiveMessage()
    {
        HttpResponseMessage response = await client.GetAsync(
            "/api/nodes?fields=id,name,type,similarity,content&count=1");

        Assert.That((int)response.StatusCode, Is.EqualTo(400),
            "similarity without query must return 400 not 500");

        using JsonDocument doc = await ReadJsonDocumentAsync(response);
        JsonElement root = doc.RootElement;

        Assert.Multiple(() => {
            Assert.That(root.GetProperty("code").GetString(), Is.EqualTo("badparameter"),
                "error code must be badparameter");
            string text = root.GetProperty("text").GetString()!;
            Assert.That(text, Does.Contain("only available").IgnoreCase,
                "error text must contain 'only available'");
            Assert.That(text, Does.Contain("?query="),
                "error text must reference '?query='");
        });
    }
}
