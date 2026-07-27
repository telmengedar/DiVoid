using System.Net.Http;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Pooshit.Http;
using Pooshit.Json;
using Pooshit.Ocelot.Entities;

namespace Backend.tests.Tests;

/// <summary>
/// Load-bearing tests for optional direction (<see cref="LinkType"/>) and carried
/// <see cref="NodeLink.Context"/> on graph edges (DiVoid task #7119).
///
/// Covers:
///   - Default/back-compat: no params → linkType="None", context=null.
///   - Directed + context: Unidirectional and Bidirectional round-trip through GET /links.
///   - Existing-pair re-link with params stays an idempotent no-op (bug #702 contract, D5).
///   - Inline links at node creation still produce None/null edges (D7).
///   - Schema/read-back: a link row inserted without the new columns reads back as
///     LinkType=None, Context=null (A2/A3 default-value contract).
/// </summary>
[TestFixture]
public class NodeLinkTypeAndContextTests
{
    // -----------------------------------------------------------------------
    // HTTP-layer tests
    // -----------------------------------------------------------------------

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

    async Task<long> CreateNodeAsync(string name, long[]? links = null)
    {
        NodeDetails created = await http.Post<NodeDetails, NodeDetails>(
            $"{TestSetup.BaseUrl}/api/nodes",
            new NodeDetails { Type = "task", Name = name, Links = links },
            new HttpOptions());
        return created.Id;
    }

    Task<HttpResponseMessage> PostLinkAsync(long sourceId, long targetId, string query = "")
        => http.Post<long, HttpResponseMessage>($"{TestSetup.BaseUrl}/api/nodes/{sourceId}/links{query}", targetId);

    async Task<LinkAdjacencyWithTypeDto> GetSingleLinkAsync(long sourceId, long targetId)
    {
        string body = await http.Get<string>($"{TestSetup.BaseUrl}/api/nodes/links?ids={sourceId},{targetId}");
        Page<LinkAdjacencyWithTypeDto> page = Json.Read<Page<LinkAdjacencyWithTypeDto>>(body);
        return page.Result.First(l =>
            l.SourceId == sourceId && l.TargetId == targetId ||
            l.SourceId == targetId && l.TargetId == sourceId);
    }

    // -----------------------------------------------------------------------
    // Default / back-compat
    // -----------------------------------------------------------------------

    [Test]
    public async Task PostLink_NoParams_ReportsNoneAndNullContext()
    {
        long a = await CreateNodeAsync("LinkTypeDefaultA");
        long b = await CreateNodeAsync("LinkTypeDefaultB");

        HttpResponseMessage resp = await PostLinkAsync(a, b);
        Assert.That((int) resp.StatusCode, Is.InRange(200, 299));

        LinkAdjacencyWithTypeDto link = await GetSingleLinkAsync(a, b);
        Assert.That(link.LinkType, Is.EqualTo(LinkType.None), "default create must report linkType=None");
        Assert.That(link.Context, Is.Null, "default create must report context=null");
    }

    // -----------------------------------------------------------------------
    // Directed + context
    // -----------------------------------------------------------------------

    [Test]
    public async Task PostLink_Unidirectional_RoundTripsExactValues()
    {
        long a = await CreateNodeAsync("LinkTypeUniA");
        long b = await CreateNodeAsync("LinkTypeUniB");

        HttpResponseMessage resp = await PostLinkAsync(a, b, "?linkType=Unidirectional&context=subtask");
        Assert.That((int) resp.StatusCode, Is.InRange(200, 299));

        LinkAdjacencyWithTypeDto link = await GetSingleLinkAsync(a, b);
        Assert.That(link.LinkType, Is.EqualTo(LinkType.Unidirectional));
        Assert.That(link.Context, Is.EqualTo("subtask"));
        Assert.That(link.SourceId, Is.EqualTo(a), "direction must be preserved source→target");
        Assert.That(link.TargetId, Is.EqualTo(b));
    }

    [Test]
    public async Task PostLink_Bidirectional_RoundTripsExactValues()
    {
        long a = await CreateNodeAsync("LinkTypeBiA");
        long b = await CreateNodeAsync("LinkTypeBiB");

        HttpResponseMessage resp = await PostLinkAsync(a, b, "?linkType=Bidirectional&context=references");
        Assert.That((int) resp.StatusCode, Is.InRange(200, 299));

        LinkAdjacencyWithTypeDto link = await GetSingleLinkAsync(a, b);
        Assert.That(link.LinkType, Is.EqualTo(LinkType.Bidirectional));
        Assert.That(link.Context, Is.EqualTo("references"));
    }

    // -----------------------------------------------------------------------
    // Existing-pair re-link with params is a no-op (D5 / bug #702)
    // -----------------------------------------------------------------------

    [Test]
    public async Task PostLink_ReLinkExistingPairWithParams_IsNoOp()
    {
        long a = await CreateNodeAsync("LinkTypeReLinkA");
        long b = await CreateNodeAsync("LinkTypeReLinkB");

        // first create — default, undirected/contextless
        HttpResponseMessage first = await PostLinkAsync(a, b);
        Assert.That((int) first.StatusCode, Is.InRange(200, 299));

        // re-link the same pair with directed params — must be silently dropped (D5)
        HttpResponseMessage second = await PostLinkAsync(a, b, "?linkType=Bidirectional&context=shouldNotApply");
        Assert.That((int) second.StatusCode, Is.InRange(200, 299),
            "re-link of an existing pair must remain a 2xx idempotent no-op (bug #702 regression)");

        LinkAdjacencyWithTypeDto link = await GetSingleLinkAsync(a, b);
        Assert.That(link.LinkType, Is.EqualTo(LinkType.None),
            "params on a re-link of an already-existing pair must be dropped (D5)");
        Assert.That(link.Context, Is.Null,
            "params on a re-link of an already-existing pair must be dropped (D5)");
    }

    // -----------------------------------------------------------------------
    // Inline links at node creation still produce None/null edges (D7)
    // -----------------------------------------------------------------------

    [Test]
    public async Task CreateNode_WithInlineLinks_ProducesNoneAndNullContextEdge()
    {
        long target = await CreateNodeAsync("LinkTypeInlineTarget");
        long source = await CreateNodeAsync("LinkTypeInlineSource", links: [target]);

        LinkAdjacencyWithTypeDto link = await GetSingleLinkAsync(source, target);
        Assert.That(link.LinkType, Is.EqualTo(LinkType.None), "inline links at creation must remain undirected");
        Assert.That(link.Context, Is.Null, "inline links at creation must remain contextless");
    }

    // -----------------------------------------------------------------------
    // Schema / read-back — a legacy-shaped row (columns not set) reads back
    // as LinkType=None, Context=null (A2/A3 default-value contract).
    // -----------------------------------------------------------------------

    [Test]
    public async Task NodeLink_RowInsertedWithoutNewColumns_ReadsBackAsNoneAndNull()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = new(fixture.EntityManager, new EmbeddingCapability(false));

        NodeDetails a = await svc.CreateNode(new NodeDetails { Type = "task", Name = "SchemaLinkA" }, callerId: 0);
        NodeDetails b = await svc.CreateNode(new NodeDetails { Type = "task", Name = "SchemaLinkB" }, callerId: 0);

        // simulate a row written before this feature existed — only the original two columns set
        await fixture.EntityManager.Insert<NodeLink>()
                     .Columns(l => l.SourceId, l => l.TargetId)
                     .Values(a.Id, b.Id)
                     .ExecuteAsync();

        NodeLink? fetched = null;
        int count = 0;
        await foreach (NodeLink row in fixture.EntityManager
                                              .Load<NodeLink>(l => l.SourceId, l => l.TargetId, l => l.LinkType, l => l.Context)
                                              .Where(l => l.SourceId == a.Id && l.TargetId == b.Id)
                                              .ExecuteEntitiesAsync())
        {
            fetched = row;
            count++;
        }

        Assert.That(count, Is.EqualTo(1), "inserted row must be retrievable");
        Assert.That(fetched!.LinkType, Is.EqualTo(LinkType.None),
            "a link row that never set LinkType must read back as None (schema default)");
        Assert.That(fetched.Context, Is.Null,
            "a link row that never set Context must read back as null (schema default)");
    }
}

/// <summary>
/// link adjacency as returned by GET /api/nodes/links, extended with the new
/// linkType + context fields (DiVoid #7119).
/// </summary>
class LinkAdjacencyWithTypeDto
{
    public long SourceId { get; set; }
    public long TargetId { get; set; }
    public LinkType LinkType { get; set; }
    public string? Context { get; set; }
}
