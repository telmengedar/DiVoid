using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Errors;
using Pooshit.Http;
using Pooshit.Json;

namespace Backend.tests.Tests;

/// <summary>
/// tests for the semantic search feature on <c>GET /api/nodes</c> (task #183).
///
/// All tests run against the SQLite in-memory fixture.  Postgres-only paths
/// (the actual <c>embedding()</c> / <c>&lt;=&gt;</c> execution) cannot be
/// exercised in CI — they are gated by <see cref="IEmbeddingCapability.IsEnabled"/>
/// and deferred to a manual smoke test on the live Postgres instance before merge.
///
/// SQL-construction-level assertions (operation tree shape, ORDER BY criteria,
/// IS NOT NULL predicate) are not available in this codebase because there is no
/// IDbClient mock harness yet (DiVoid task #126 is still open).  The tests here
/// cover the seams that ARE exercisable on SQLite:
/// <list type="bullet">
///   <item>capability guard path (SQLite → 400)</item>
///   <item>minSimilarity-without-query guard</item>
///   <item>mapper field-set shape (no DB required)</item>
///   <item>unchanged listing regression when Query is absent</item>
/// </list>
/// </summary>
[TestFixture]
public class SemanticSearchTests
{
    static readonly IEmbeddingCapability EnabledCapability = new EmbeddingCapability(true);
    static readonly IEmbeddingCapability DisabledCapability = new EmbeddingCapability(false);

    static NodeService MakeService(DatabaseFixture fixture, IEmbeddingCapability? capability = null)
        => new(fixture.EntityManager, capability ?? DisabledCapability);

    static Task<NodeDetails> CreateNode(NodeService svc, string type = "task", string name = "node")
        => svc.CreateNode(new NodeDetails { Type = type, Name = name });

    static async Task<List<NodeDetails>> CollectPage(
        Pooshit.AspNetCore.Services.Formatters.DataStream.AsyncPageResponseWriter<NodeDetails> writer)
    {
        byte[] buffer;
        using (MemoryStream ms = new())
        {
            await writer.Write(ms);
            buffer = ms.ToArray();
        }
        using MemoryStream readMs = new(buffer);
        string json = await new StreamReader(readMs).ReadToEndAsync();
        Page<NodeDetails> page = Json.Read<Page<NodeDetails>>(json);
        return page.Result?.ToList() ?? [];
    }

    // -----------------------------------------------------------------------
    // 1. Capability disabled + Query absent → unchanged listing (regression)
    // -----------------------------------------------------------------------

    [Test]
    public async Task ListPaged_QueryAbsent_CapabilityDisabled_ReturnsNormalListing()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture, DisabledCapability);

        NodeDetails n1 = await CreateNode(svc, name: "Alpha");
        NodeDetails n2 = await CreateNode(svc, name: "Beta");

        var writer = await svc.ListPaged(new NodeFilter { Count = 100 });
        List<NodeDetails> items = await CollectPage(writer);

        long[] ids = items.Select(n => n.Id).ToArray();
        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(n1.Id));
            Assert.That(ids, Does.Contain(n2.Id));
            // standard listing: similarity must be null
            Assert.That(items.All(n => n.Similarity == null), Is.True,
                "similarity must be null on all items when Query is absent");
        });
    }

    // -----------------------------------------------------------------------
    // 2. Capability disabled + Query populated → InvalidOperationException (→ HTTP 400)
    //    (verified both at service and at HTTP level)
    // -----------------------------------------------------------------------

    [Test]
    public void ListPaged_QueryPopulated_CapabilityDisabled_ThrowsInvalidOperationException()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture, DisabledCapability);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ListPaged(new NodeFilter { Query = "find something useful", Count = 10 }));
    }

    [Test]
    public void ListPaged_QueryPopulated_CapabilityDisabled_ExceptionMessageMentionsPostgres()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture, DisabledCapability);

        InvalidOperationException ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ListPaged(new NodeFilter { Query = "find something", Count = 10 }));

        Assert.That(ex!.Message, Does.Contain("Postgres").IgnoreCase.Or.Contain("embedding function").IgnoreCase,
            "error message must identify the unsupported capability (Postgres / embedding function)");
    }

    [Test]
    public void ListPagedByPath_QueryPopulated_CapabilityDisabled_ThrowsInvalidOperationException()
    {
        // Capability gate must fire on query regardless of whether Path is also present (arch doc §10).
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture, DisabledCapability);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ListPagedByPath(
                new NodePathFilter { Path = "[type:task]", Query = "find something", Count = 10 },
                CancellationToken.None));
    }

    // -----------------------------------------------------------------------
    // 3. minSimilarity without query → InvalidOperationException (→ HTTP 400)
    // -----------------------------------------------------------------------

    [Test]
    public void ListPaged_MinSimilarityWithoutQuery_ThrowsInvalidOperationException()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture, DisabledCapability);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ListPaged(new NodeFilter { MinSimilarity = 0.5f, Count = 10 }));
    }

    [Test]
    public void ListPaged_MinSimilarityWithoutQuery_ExceptionMessageMentionsQuery()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture, DisabledCapability);

        InvalidOperationException ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ListPaged(new NodeFilter { MinSimilarity = 0.7f, Count = 10 }));

        Assert.That(ex!.Message, Does.Contain("query").IgnoreCase,
            "error message must mention the required 'query' parameter");
    }

    [Test]
    public void ListPagedByPath_MinSimilarityWithoutQuery_ThrowsInvalidOperationException()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture, DisabledCapability);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ListPagedByPath(
                new NodePathFilter { Path = "[type:task]", MinSimilarity = 0.5f, Count = 10 },
                CancellationToken.None));
    }

    // -----------------------------------------------------------------------
    // 4. NodeMapper field set — unit tests (no DB needed)
    // -----------------------------------------------------------------------

    [Test]
    public void NodeMapper_NoQuery_DoesNotIncludeSimilarityField()
    {
        // Without a query filter the mapper must not register a "similarity" key.
        NodeMapper mapper = new();
        Assert.Throws<KeyNotFoundException>(() => _ = mapper["similarity"],
            "mapper without Query must not expose a 'similarity' field mapping");
    }

    [Test]
    public void NodeMapper_WithQuery_IncludesSimilarityField()
    {
        NodeMapper mapper = new(new NodeFilter { Query = "some search text" });
        Assert.DoesNotThrow(() => _ = mapper["similarity"],
            "mapper with Query must expose a 'similarity' field mapping");
    }

    [Test]
    public void NodeMapper_WithQuery_SimilarityFieldPresent_AllStandardFieldsAlsoPresent()
    {
        NodeMapper mapper = new(new NodeFilter { Query = "some search text" });
        // Standard fields must still be accessible alongside the similarity field
        Assert.Multiple(() => {
            Assert.DoesNotThrow(() => _ = mapper["id"]);
            Assert.DoesNotThrow(() => _ = mapper["type"]);
            Assert.DoesNotThrow(() => _ = mapper["name"]);
            Assert.DoesNotThrow(() => _ = mapper["status"]);
            Assert.DoesNotThrow(() => _ = mapper["contentType"]);
            Assert.DoesNotThrow(() => _ = mapper["similarity"]);
        });
    }

    [Test]
    public void NodeMapper_WithEmptyQuery_DoesNotIncludeSimilarityField()
    {
        // empty-string and whitespace-only queries are treated as absent (IsNullOrWhiteSpace gate)
        NodeMapper mapperEmpty = new(new NodeFilter { Query = "" });
        NodeMapper mapperWhitespace = new(new NodeFilter { Query = "   " });

        Assert.Throws<KeyNotFoundException>(() => _ = mapperEmpty["similarity"],
            "mapper with empty Query must not expose similarity field");
        Assert.Throws<KeyNotFoundException>(() => _ = mapperWhitespace["similarity"],
            "mapper with whitespace-only Query must not expose similarity field");
    }

    [Test]
    public void NodeMapper_NullFilter_DoesNotIncludeSimilarityField()
    {
        NodeMapper mapper = new(null);
        Assert.Throws<KeyNotFoundException>(() => _ = mapper["similarity"],
            "mapper with null filter must not expose similarity field");
    }

    // -----------------------------------------------------------------------
    // 5. NodeDetails.Similarity — serialization shape
    // -----------------------------------------------------------------------

    [Test]
    public void NodeDetails_Similarity_Null_OmittedFromJson()
    {
        // When Similarity is null it must be omitted from the response JSON
        // (JsonIgnore(Condition = WhenWritingNull)).
        NodeDetails details = new() { Id = 1, Name = "test", Type = "task" };
        // Similarity is null by default
        string json = System.Text.Json.JsonSerializer.Serialize(details);
        Assert.That(json, Does.Not.Contain("similarity").IgnoreCase,
            "null Similarity must not appear in serialized JSON");
    }

    [Test]
    public void NodeDetails_Similarity_Present_IncludedInJson()
    {
        NodeDetails details = new() { Id = 1, Name = "test", Type = "task", Similarity = 0.87f };
        string json = System.Text.Json.JsonSerializer.Serialize(details);
        Assert.That(json, Does.Contain("similarity").IgnoreCase,
            "non-null Similarity must appear in serialized JSON");
    }

    // -----------------------------------------------------------------------
    // 6. HTTP integration — capability-disabled (SQLite) returns 400 badparameter
    // -----------------------------------------------------------------------

    [Test]
    public async Task Http_QueryPopulated_CapabilityDisabled_Returns400()
    {
        using WebApplicationFactory<Program> factory = TestSetup.CreateTestFactory();
        IHttpService http = TestSetup.HttpServiceFor(factory);

        HttpResponseMessage resp = await http.Get<HttpResponseMessage>(
            $"{TestSetup.BaseUrl}/api/nodes?query=find+something");

        Assert.That((int) resp.StatusCode, Is.EqualTo(400),
            "semantic search on SQLite must return HTTP 400");
    }

    [Test]
    public async Task Http_QueryPopulated_CapabilityDisabled_ReturnsBadParameterCode()
    {
        using WebApplicationFactory<Program> factory = TestSetup.CreateTestFactory();
        IHttpService http = TestSetup.HttpServiceFor(factory);

        HttpResponseMessage resp = await http.Get<HttpResponseMessage>(
            $"{TestSetup.BaseUrl}/api/nodes?query=find+something");
        string json = await resp.Content.ReadAsStringAsync();
        ErrorResponse error = Json.Read<ErrorResponse>(json);

        Assert.That(error.Code, Is.EqualTo("badparameter"),
            "error code must be badparameter for semantic search on SQLite");
    }

    [Test]
    public async Task Http_QueryPopulated_CapabilityDisabled_ErrorMentionsEmbeddingFunction()
    {
        using WebApplicationFactory<Program> factory = TestSetup.CreateTestFactory();
        IHttpService http = TestSetup.HttpServiceFor(factory);

        HttpResponseMessage resp = await http.Get<HttpResponseMessage>(
            $"{TestSetup.BaseUrl}/api/nodes?query=something");
        string json = await resp.Content.ReadAsStringAsync();
        ErrorResponse error = Json.Read<ErrorResponse>(json);

        Assert.That(error.Text, Does.Contain("embedding").IgnoreCase.Or.Contain("Postgres").IgnoreCase,
            "error text must reference the unsupported capability");
    }

    [Test]
    public async Task Http_MinSimilarityWithoutQuery_Returns400()
    {
        using WebApplicationFactory<Program> factory = TestSetup.CreateTestFactory();
        IHttpService http = TestSetup.HttpServiceFor(factory);

        HttpResponseMessage resp = await http.Get<HttpResponseMessage>(
            $"{TestSetup.BaseUrl}/api/nodes?minSimilarity=0.5");

        Assert.That((int) resp.StatusCode, Is.EqualTo(400),
            "minSimilarity without query must return HTTP 400");
    }

    [Test]
    public async Task Http_MinSimilarityWithoutQuery_ReturnsBadParameterCode()
    {
        using WebApplicationFactory<Program> factory = TestSetup.CreateTestFactory();
        IHttpService http = TestSetup.HttpServiceFor(factory);

        HttpResponseMessage resp = await http.Get<HttpResponseMessage>(
            $"{TestSetup.BaseUrl}/api/nodes?minSimilarity=0.7");
        string json = await resp.Content.ReadAsStringAsync();
        ErrorResponse error = Json.Read<ErrorResponse>(json);

        Assert.That(error.Code, Is.EqualTo("badparameter"));
    }

    [Test]
    public async Task Http_QueryAbsent_Returns200WithNormalListing()
    {
        // Regression: standard listing must work exactly as before with Query absent.
        using WebApplicationFactory<Program> factory = TestSetup.CreateTestFactory();
        IHttpService http = TestSetup.HttpServiceFor(factory);

        // POST a node so the listing is non-empty
        NodeDetails created = await http.Post<NodeDetails, NodeDetails>(
            $"{TestSetup.BaseUrl}/api/nodes",
            new NodeDetails { Type = "task", Name = "RegressionNode" },
            new HttpOptions());

        HttpResponseMessage resp = await http.Get<HttpResponseMessage>(
            $"{TestSetup.BaseUrl}/api/nodes?id={created.Id}");

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            "standard listing (no query) must return 200 on SQLite");

        string json = await resp.Content.ReadAsStringAsync();
        Page<NodeDetails> page = Json.Read<Page<NodeDetails>>(json);
        List<NodeDetails> items = page.Result?.ToList() ?? [];

        Assert.Multiple(() => {
            Assert.That(items.Any(n => n.Id == created.Id), Is.True,
                "created node must appear in standard listing");
            Assert.That(items.All(n => n.Similarity == null), Is.True,
                "similarity must be null for all items when Query is absent");
        });
    }

    // -----------------------------------------------------------------------
    // 7. Path + Query — capability guard fires regardless of Path presence
    // -----------------------------------------------------------------------

    [Test]
    public async Task Http_PathAndQuery_CapabilityDisabled_Returns400()
    {
        using WebApplicationFactory<Program> factory = TestSetup.CreateTestFactory();
        IHttpService http = TestSetup.HttpServiceFor(factory);

        // Both path and query supplied — capability gate must fire before path parsing
        HttpResponseMessage resp = await http.Get<HttpResponseMessage>(
            $"{TestSetup.BaseUrl}/api/nodes?path={Uri.EscapeDataString("[type:task]")}&query=find+something");

        Assert.That((int) resp.StatusCode, Is.EqualTo(400),
            "path + query on SQLite must return 400 — capability gate fires regardless of Path");
    }

    // -----------------------------------------------------------------------
    // NOTE: The following test scenarios are deferred to Postgres smoke test
    // because they require actual embedding() execution or the IDbClient mock
    // harness (DiVoid task #126, currently open):
    //
    //   - ORDER BY similarity DESC, id ASC shape when Query is present
    //   - Embedding IS NOT NULL predicate when Query is present
    //   - MinSimilarity floor predicate (>= 0.7 etc.) in SQL
    //   - Path-mode + Query compound operation SQL shape
    //   - Actual similarity scores returned by Postgres
    //
    // These are not skipped silently — they are explicitly deferred here with
    // explanation per the project's test-discipline standard.
    // -----------------------------------------------------------------------
}
