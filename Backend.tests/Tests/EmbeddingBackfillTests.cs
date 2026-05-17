using System.Text;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// tests for <see cref="EmbeddingBackfillService"/>.
/// all tests run on SQLite (via <see cref="DatabaseFixture"/>).
/// the Postgres embedding() call path is never reached — the capability flag is false.
/// </summary>
[TestFixture]
public class EmbeddingBackfillTests
{
    static readonly IEmbeddingCapability DisabledCapability = new EmbeddingCapability(false);
    static readonly IEmbeddingCapability EnabledCapability  = new EmbeddingCapability(true);

    static NodeService MakeNodeService(DatabaseFixture fixture)
        => new(fixture.EntityManager, DisabledCapability);

    static EmbeddingBackfillService MakeBackfill(DatabaseFixture fixture, IEmbeddingCapability capability)
        => new(fixture.EntityManager, capability, NullLogger<EmbeddingBackfillService>.Instance);

    // -----------------------------------------------------------------------
    // 1. Capability disabled (SQLite) — exits immediately, no DB writes
    // -----------------------------------------------------------------------

    [Test]
    public async Task RunAsync_CapabilityDisabled_ExitsWithoutWriting()
    {
        using DatabaseFixture fixture = new();
        NodeService nodeSvc = MakeNodeService(fixture);
        EmbeddingBackfillService backfill = MakeBackfill(fixture, DisabledCapability);

        // Seed one text-content node — its Embedding must remain null after the backfill no-op.
        NodeDetails node = await nodeSvc.CreateNode(new NodeDetails { Type = "doc", Name = "ShouldNotBeEmbedded" });
        byte[] content = Encoding.UTF8.GetBytes("some markdown text");
        await nodeSvc.UploadContent(node.Id, "text/markdown", new MemoryStream(content));

        // Run backfill with capability disabled.
        await backfill.RunAsync();

        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == node.Id)
                                              .ExecuteEntityAsync();

        Assert.That(raw.Embedding, Is.Null,
            "Embedding must remain null — capability was disabled so RunAsync must be a no-op");
    }

    // -----------------------------------------------------------------------
    // 2. Capability enabled on SQLite — selection logic tests
    //
    // When IsEnabled = true but the database is SQLite, the service will attempt
    // to call DB.CustomFunction for qualifying nodes.  SQLite does not have the
    // embedding() function, so any attempt will throw.  We therefore test only
    // nodes that are *skipped* by the selection predicates: non-text content-type,
    // null content, or no nodes at all.  For those nodes RunAsync must complete
    // without touching the embedding column.
    //
    // The "already embedded" variant cannot be exercised on SQLite because setting
    // a 3072-element float[] value is not supported by the SQLite serialization
    // Ocelot uses.  That path is verified by the WHERE predicate filter on Postgres
    // during the production smoke check.
    // -----------------------------------------------------------------------

    [Test]
    public async Task RunAsync_CapabilityEnabled_SkipsNonTextNodesWithoutName()
    {
        // v2 candidate predicate: "name non-empty OR (content non-null AND text type)".
        // A node with non-text content AND an empty name has no embeddable surface → skipped.
        // We verify no CustomFunction call is reached (SQLite would throw if it were).
        using DatabaseFixture fixture = new();
        NodeService nodeSvc = MakeNodeService(fixture);

        // create with empty name so the v2 predicate also excludes by name
        NodeDetails node = await nodeSvc.CreateNode(new NodeDetails { Type = "asset", Name = "" });
        byte[] content = [0x89, 0x50, 0x4E, 0x47]; // PNG magic bytes
        await nodeSvc.UploadContent(node.Id, "image/png", new MemoryStream(content));

        EmbeddingBackfillService backfill = MakeBackfill(fixture, EnabledCapability);

        // No DB.CustomFunction call is reached: non-text AND empty name → skipped.
        Assert.DoesNotThrowAsync(() => backfill.RunAsync());

        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == node.Id)
                                              .ExecuteEntityAsync();

        Assert.That(raw.Embedding, Is.Null,
            "non-text content + empty name → no embeddable surface → skipped by v2 candidate predicate");
    }

    [Test]
    public async Task RunAsync_CapabilityEnabled_IncludesNonTextNodesWithName()
    {
        // v2 change: a node with non-text content but a non-empty name is now a candidate
        // (name-only embedding).  On SQLite with IsEnabled=true the UPDATE would throw;
        // verify the node IS in the candidate set by checking it passes the predicate count.
        // (we cannot execute the UPDATE on SQLite — that would call embedding())
        // Instead we assert that RunAsync with enabled capability reaches the node and throws,
        // proving it is no longer skipped.
        using DatabaseFixture fixture = new();
        NodeService nodeSvc = MakeNodeService(fixture);

        NodeDetails node = await nodeSvc.CreateNode(new NodeDetails { Type = "asset", Name = "PngWithName" });
        byte[] content = [0x89, 0x50, 0x4E, 0x47]; // PNG magic bytes
        await nodeSvc.UploadContent(node.Id, "image/png", new MemoryStream(content));

        EmbeddingBackfillService backfill = MakeBackfill(fixture, EnabledCapability);

        // v2 candidate predicate includes this node (has name) — RunAsync will attempt
        // to call embedding() on SQLite and fail.  This is the load-bearing assertion:
        // if the predicate had not changed (v1 style), RunAsync would succeed silently.
        Assert.CatchAsync(() => backfill.RunAsync(),
            "node with non-empty name is a v2 candidate; RunAsync must attempt (and fail on SQLite) to embed it");
    }

    [Test]
    public async Task RunAsync_CapabilityEnabled_SkipsNodesWithNullContentAndEmptyName()
    {
        // v2 predicate: skipped only when BOTH name is empty AND content is null/non-text.
        // A node with empty name AND no content has no embeddable surface → skipped entirely.
        using DatabaseFixture fixture = new();
        NodeService nodeSvc = MakeNodeService(fixture);

        // force-insert a node with an empty name (the API prevents it but the DB allows it)
        long nodeId = await fixture.EntityManager.Insert<Node>()
                                                 .Columns(n => n.Name, n => n.TypeId)
                                                 .Values("", 0)
                                                 .ReturnID()
                                                 .ExecuteAsync();

        EmbeddingBackfillService backfill = MakeBackfill(fixture, EnabledCapability);

        // no embeddable surface → RunAsync must complete without attempting embedding() on SQLite
        Assert.DoesNotThrowAsync(() => backfill.RunAsync(),
            "node with empty name and null content has no embeddable surface and must be skipped");

        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == nodeId)
                                              .ExecuteEntityAsync();

        Assert.That(raw.Embedding, Is.Null,
            "node with empty name + null content must remain unembedded — no embeddable surface");
    }

    [Test]
    public async Task RunAsync_CapabilityEnabled_IncludesNodesWithNameAndNullContent()
    {
        // v2 change: a node with non-empty name but null content IS a candidate (name-only embedding).
        // On SQLite with IsEnabled=true the UPDATE will fail because embedding() doesn't exist.
        // This is the load-bearing assertion: RunAsync must attempt to embed the node
        // (proving the predicate was broadened vs v1 which required Content IS NOT NULL).
        using DatabaseFixture fixture = new();
        NodeService nodeSvc = MakeNodeService(fixture);

        // Create a node without uploading content — Name is non-empty, Content is null.
        NodeDetails node = await nodeSvc.CreateNode(new NodeDetails { Type = "doc", Name = "NameNoContent" });

        EmbeddingBackfillService backfill = MakeBackfill(fixture, EnabledCapability);

        // v2 predicate includes this node → embedding() called on SQLite → fails
        Assert.CatchAsync(() => backfill.RunAsync(),
            "node with non-empty name and null content is a v2 candidate; RunAsync must attempt (and fail on SQLite) to embed it");
    }

    // -----------------------------------------------------------------------
    // 3. Idempotency contract: re-running on an empty candidate set is a no-op
    // -----------------------------------------------------------------------

    [Test]
    public async Task RunAsync_CapabilityDisabled_EmptyDb_CompletesCleanly()
    {
        // Database with no nodes at all — RunAsync should complete without exception.
        using DatabaseFixture fixture = new();
        EmbeddingBackfillService backfill = MakeBackfill(fixture, DisabledCapability);

        Assert.DoesNotThrowAsync(() => backfill.RunAsync());
    }
}
