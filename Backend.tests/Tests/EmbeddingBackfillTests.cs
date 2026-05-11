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
    public async Task RunAsync_CapabilityEnabled_SkipsNonTextNodes()
    {
        // A node with non-text ContentType must be counted as skipped (not attempted).
        // We verify this by seeding only non-text nodes and asserting RunAsync completes
        // without throwing (no CustomFunction call was reached).
        using DatabaseFixture fixture = new();
        NodeService nodeSvc = MakeNodeService(fixture);

        NodeDetails node = await nodeSvc.CreateNode(new NodeDetails { Type = "asset", Name = "PngNode" });
        byte[] content = [0x89, 0x50, 0x4E, 0x47]; // PNG magic bytes
        await nodeSvc.UploadContent(node.Id, "image/png", new MemoryStream(content));

        EmbeddingBackfillService backfill = MakeBackfill(fixture, EnabledCapability);

        // No DB.CustomFunction call is reached because IsText("image/png") == false.
        Assert.DoesNotThrowAsync(() => backfill.RunAsync());

        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == node.Id)
                                              .ExecuteEntityAsync();

        Assert.That(raw.Embedding, Is.Null,
            "non-text node must remain unembedded — it is skipped by TextContentTypePredicate");
    }

    [Test]
    public async Task RunAsync_CapabilityEnabled_SkipsNodesWithNullContent()
    {
        // A node with no content at all (Content is null) must be skipped.
        using DatabaseFixture fixture = new();
        NodeService nodeSvc = MakeNodeService(fixture);

        // Create a node but do not upload any content — Content remains null.
        NodeDetails node = await nodeSvc.CreateNode(new NodeDetails { Type = "doc", Name = "NoContent" });

        EmbeddingBackfillService backfill = MakeBackfill(fixture, EnabledCapability);

        Assert.DoesNotThrowAsync(() => backfill.RunAsync());

        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == node.Id)
                                              .ExecuteEntityAsync();

        Assert.That(raw.Embedding, Is.Null,
            "content-less node must remain unembedded");
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
