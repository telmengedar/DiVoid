using System.Text;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pooshit.Ocelot.Entities;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// tests for <see cref="EmbeddingBackfillService"/>.
/// all tests run on SQLite (via <see cref="DatabaseFixture"/>); embedding writes are either never reached (capability disabled, nothing to embed)
/// or executed as marker writes by <see cref="EmbeddingWriteMarkerClient"/>.
/// </summary>
[TestFixture, Parallelizable]
public class EmbeddingBackfillTests
{
    static readonly IEmbeddingCapability DisabledCapability = new EmbeddingCapability(false);
    static readonly IEmbeddingCapability EnabledCapability  = new EmbeddingCapability(true);

    static NodeService MakeNodeService(DatabaseFixture fixture)
        => new(fixture.EntityManager, DisabledCapability, EmbeddingProducers.Function);

    static EmbeddingBackfillService MakeBackfill(DatabaseFixture fixture, IEmbeddingCapability capability)
        => new(fixture.EntityManager, capability, new EmbeddingModelGuard(fixture.EntityManager, capability, NullLogger<EmbeddingModelGuard>.Instance), NullLogger<EmbeddingBackfillService>.Instance, EmbeddingProducers.Function);

    // -----------------------------------------------------------------------
    // 1. Capability disabled (SQLite) — exits immediately, no DB writes
    // -----------------------------------------------------------------------

    [Test, Parallelizable]
    public async Task RunAsync_CapabilityDisabled_ExitsWithoutWriting()
    {
        using DatabaseFixture fixture = new();
        NodeService nodeSvc = MakeNodeService(fixture);
        EmbeddingBackfillService backfill = MakeBackfill(fixture, DisabledCapability);

        // Seed one text-content node — its Embedding must remain null after the backfill no-op.
        NodeDetails node = await nodeSvc.CreateNode(new NodeDetails { Type = "doc", Name = "ShouldNotBeEmbedded" }, callerId: 0);
        byte[] content = Encoding.UTF8.GetBytes("some markdown text");
        await nodeSvc.UploadContent(node.Id, "text/markdown", new MemoryStream(content), callerId: 0, isAdmin: true);

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

    [Test, Parallelizable]
    public async Task RunAsync_CapabilityEnabled_SkipsNonTextNodesWithoutName()
    {
        using DatabaseFixture fixture = new();
        NodeService nodeSvc = MakeNodeService(fixture);

        NodeDetails node = await nodeSvc.CreateNode(new NodeDetails { Type = "asset", Name = "" }, callerId: 0);
        byte[] content = [0x89, 0x50, 0x4E, 0x47]; // PNG magic bytes
        await nodeSvc.UploadContent(node.Id, "image/png", new MemoryStream(content), callerId: 0, isAdmin: true);

        EmbeddingBackfillService backfill = MakeBackfill(fixture, EnabledCapability);

        Assert.DoesNotThrowAsync(() => backfill.RunAsync());

        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == node.Id)
                                              .ExecuteEntityAsync();

        Assert.That(raw.Embedding, Is.Null,
            "non-text content + empty name → no embeddable surface → skipped by v2 candidate predicate");
    }

    [Test, Parallelizable]
    public async Task RunAsync_CapabilityEnabled_IncludesNonTextNodesWithName()
    {
        using DatabaseFixture fixture = new();
        NodeService nodeSvc = MakeNodeService(fixture);

        NodeDetails node = await nodeSvc.CreateNode(new NodeDetails { Type = "asset", Name = "PngWithName" }, callerId: 0);
        byte[] content = [0x89, 0x50, 0x4E, 0x47]; // PNG magic bytes
        await nodeSvc.UploadContent(node.Id, "image/png", new MemoryStream(content), callerId: 0, isAdmin: true);

        EmbeddingBackfillService backfill = MakeBackfill(fixture, EnabledCapability);

        // v2 candidate predicate includes this node (has name) — RunAsync will attempt
        // to call embedding() on SQLite and fail.  This is the load-bearing assertion:
        // if the predicate had not changed (v1 style), RunAsync would succeed silently.
        Assert.CatchAsync(() => backfill.RunAsync(),
            "node with non-empty name is a v2 candidate; RunAsync must attempt (and fail on SQLite) to embed it");
    }

    [Test, Parallelizable]
    public async Task RunAsync_CapabilityEnabled_SkipsNodesWithNullContentAndEmptyName()
    {
        using DatabaseFixture fixture = new();
        NodeService nodeSvc = MakeNodeService(fixture);

        long nodeId = await fixture.EntityManager.Insert<Node>()
                                                 .Columns(n => n.Name, n => n.TypeId)
                                                 .Values("", 0)
                                                 .ReturnID()
                                                 .ExecuteAsync();

        EmbeddingBackfillService backfill = MakeBackfill(fixture, EnabledCapability);

        Assert.DoesNotThrowAsync(() => backfill.RunAsync(),
            "node with empty name and null content has no embeddable surface and must be skipped");

        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == nodeId)
                                              .ExecuteEntityAsync();

        Assert.That(raw.Embedding, Is.Null,
            "node with empty name + null content must remain unembedded — no embeddable surface");
    }

    [Test, Parallelizable]
    public async Task RunAsync_CapabilityEnabled_IncludesNodesWithNameAndNullContent()
    {
        // v2 change: a node with non-empty name but null content IS a candidate (name-only embedding).
        // On SQLite with IsEnabled=true the UPDATE will fail because embedding() doesn't exist.
        // This is the load-bearing assertion: RunAsync must attempt to embed the node
        // (proving the predicate was broadened vs v1 which required Content IS NOT NULL).
        using DatabaseFixture fixture = new();
        NodeService nodeSvc = MakeNodeService(fixture);

        NodeDetails node = await nodeSvc.CreateNode(new NodeDetails { Type = "doc", Name = "NameNoContent" }, callerId: 0);

        EmbeddingBackfillService backfill = MakeBackfill(fixture, EnabledCapability);

        Assert.CatchAsync(() => backfill.RunAsync(),
            "node with non-empty name and null content is a v2 candidate; RunAsync must attempt (and fail on SQLite) to embed it");
    }

    // -----------------------------------------------------------------------
    // 3. Idempotency contract: re-running on an empty candidate set is a no-op
    // -----------------------------------------------------------------------

    [Test, Parallelizable]
    public async Task RunAsync_CapabilityDisabled_EmptyDb_CompletesCleanly()
    {
        // Database with no nodes at all — RunAsync should complete without exception.
        using DatabaseFixture fixture = new();
        EmbeddingBackfillService backfill = MakeBackfill(fixture, DisabledCapability);

        Assert.DoesNotThrowAsync(() => backfill.RunAsync());
    }

    static async Task<long> Seed(DatabaseFixture fixture, string name, string? contentType, byte[]? content)
    {
        NodeService nodeSvc = MakeNodeService(fixture);
        NodeDetails node = await nodeSvc.CreateNode(new NodeDetails { Type = "doc", Name = name }, callerId: 0);
        if (content != null)
            await nodeSvc.UploadContent(node.Id, contentType!, new MemoryStream(content), callerId: 0, isAdmin: true);
        return node.Id;
    }

    static byte[] Bytes(int length) => Encoding.UTF8.GetBytes(new string('x', length));

    static EmbeddingBackfillService MakeRecordingBackfill(DatabaseFixture fixture, Func<Task>? beforeWrite = null, ILogger<EmbeddingBackfillService>? logger = null)
    {
        IEntityManager recording = new EntityManager(EmbeddingWriteMarkerClient.Wrap(fixture.EntityManager.DBClient, beforeWrite));
        return new EmbeddingBackfillService(recording, EnabledCapability, new EmbeddingModelGuard(recording, EnabledCapability, NullLogger<EmbeddingModelGuard>.Instance), logger ?? NullLogger<EmbeddingBackfillService>.Instance, EmbeddingProducers.Function);
    }

    static async Task<long[]> EmbeddedIds(DatabaseFixture fixture)
    {
        List<long> ids = [];
        await foreach (Node node in fixture.EntityManager.Load<Node>(n => n.Id)
                                                         .Where(n => n.Substance == "embedded")
                                                         .ExecuteEntitiesAsync())
            ids.Add(node.Id);
        return ids.Order().ToArray();
    }

    [Test, Parallelizable]
    public async Task NoOptions_SelectionUnchanged()
    {
        using DatabaseFixture fixture = new();
        long named = await Seed(fixture, "named", null, null);
        long unnamedText = await Seed(fixture, "", "text/plain", Bytes(10));
        long namedImage = await Seed(fixture, "image", "image/png", [0x89, 0x50]);
        await Seed(fixture, "", "image/png", [0x89, 0x50]);
        await Seed(fixture, "", null, null);
        long longNode = await Seed(fixture, "long", "text/plain", Bytes(9000));
        EmbeddingBackfillService backfill = MakeRecordingBackfill(fixture);

        await backfill.RunAsync();

        Assert.That(await EmbeddedIds(fixture), Is.EqualTo(new[] { named, unnamedText, namedImage, longNode }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: only rows which may compose to more than MaxLength are selected, measured as twice the name characters + 2 + content bytes")]
    public async Task LongOnly_SelectsOnlyRowsOverMaxLength()
    {
        using DatabaseFixture fixture = new();
        await Seed(fixture, "ab", "text/plain", Bytes(100));
        await Seed(fixture, "ab", "text/plain", Bytes(EmbeddingInputComposer.MaxLength - 6));
        long namedOverLimit = await Seed(fixture, "ab", "text/plain", Bytes(EmbeddingInputComposer.MaxLength - 5));
        await Seed(fixture, "", "text/plain", Bytes(EmbeddingInputComposer.MaxLength));
        long unnamedOverLimit = await Seed(fixture, "", "text/plain", Bytes(EmbeddingInputComposer.MaxLength + 1));
        await Seed(fixture, new string('n', EmbeddingInputComposer.MaxLength + 10), null, null);
        long multiByteOverLimitInBytes = await Seed(fixture, "", "text/plain", Encoding.UTF8.GetBytes(new string('ä', 5000)));
        EmbeddingBackfillService backfill = MakeRecordingBackfill(fixture);

        await backfill.RunAsync(longOnly: true);

        Assert.That(await EmbeddedIds(fixture), Is.EqualTo(new[] { namedOverLimit, unnamedOverLimit, multiByteOverLimitInBytes }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16209: a name of astral characters counts two UTF-16 units each in the composer, so it must not shrink the long-only measure")]
    public async Task LongOnly_NameOfAstralCharacters_IsMeasuredInUtf16Units()
    {
        using DatabaseFixture fixture = new();
        string emoji = string.Concat(Enumerable.Repeat("😀", 3));
        long astralOverLimit = await Seed(fixture, emoji, "text/plain", Bytes(EmbeddingInputComposer.MaxLength - 5));
        EmbeddingBackfillService backfill = MakeRecordingBackfill(fixture);

        await backfill.RunAsync(longOnly: true);

        Assert.That(await EmbeddedIds(fixture), Is.EqualTo(new[] { astralOverLimit }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16209: non-text content cannot be windowed, so a long named image is not re-embedded under long-only")]
    public async Task LongOnly_NonTextContent_IsNotSelected()
    {
        using DatabaseFixture fixture = new();
        await Seed(fixture, "image", "image/png", Bytes(EmbeddingInputComposer.MaxLength + 100));
        long text = await Seed(fixture, "text", "text/plain", Bytes(EmbeddingInputComposer.MaxLength + 100));
        EmbeddingBackfillService backfill = MakeRecordingBackfill(fixture);

        await backfill.RunAsync(longOnly: true);

        Assert.That(await EmbeddedIds(fixture), Is.EqualTo(new[] { text }));
    }

    [Test, Parallelizable]
    public async Task Ids_RestrictsSelectionToGivenIds()
    {
        using DatabaseFixture fixture = new();
        long first = await Seed(fixture, "first", null, null);
        await Seed(fixture, "second", null, null);
        long third = await Seed(fixture, "third", null, null);
        EmbeddingBackfillService backfill = MakeRecordingBackfill(fixture);

        await backfill.RunAsync(ids: [first, third]);

        Assert.That(await EmbeddedIds(fixture), Is.EqualTo(new[] { first, third }));
    }

    static async Task<long> SeedSkipEmbedding(DatabaseFixture fixture, string name)
    {
        NodeDetails node = await MakeNodeService(fixture).CreateNode(new NodeDetails { Type = "doc", Name = name, SkipEmbedding = true }, callerId: 0);
        return node.Id;
    }

    [Test, Parallelizable]
    [Description("DiVoid #22474: a node flagged with skipEmbedding is not a candidate, so it is never re-embedded")]
    public async Task SkipEmbeddingNode_NotSelected()
    {
        using DatabaseFixture fixture = new();
        long embedded = await Seed(fixture, "embedded", null, null);
        await SeedSkipEmbedding(fixture, "skipped");
        EmbeddingBackfillService backfill = MakeRecordingBackfill(fixture);

        await backfill.RunAsync();

        Assert.That(await EmbeddedIds(fixture), Is.EqualTo(new[] { embedded }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #22474: an explicit id of a flagged node does not make it a candidate either")]
    public async Task Ids_SkipEmbeddingNode_NotSelected()
    {
        using DatabaseFixture fixture = new();
        long embedded = await Seed(fixture, "embedded", null, null);
        long skipped = await SeedSkipEmbedding(fixture, "skipped");
        EmbeddingBackfillService backfill = MakeRecordingBackfill(fixture);

        await backfill.RunAsync(ids: [embedded, skipped]);

        Assert.That(await EmbeddedIds(fixture), Is.EqualTo(new[] { embedded }));
    }

    [Test, Parallelizable]
    public async Task IdsAndLongOnly_AreCombined()
    {
        using DatabaseFixture fixture = new();
        long longListed = await Seed(fixture, "long", "text/plain", Bytes(9000));
        await Seed(fixture, "long", "text/plain", Bytes(9000));
        long shortListed = await Seed(fixture, "short", "text/plain", Bytes(10));
        EmbeddingBackfillService backfill = MakeRecordingBackfill(fixture);

        await backfill.RunAsync(ids: [longListed, shortListed], longOnly: true);

        Assert.That(await EmbeddedIds(fixture), Is.EqualTo(new[] { longListed }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16209: a charset parameter or upper case in the content type must not hide text content, as TextContentTypePredicate.IsText")]
    public async Task UnnamedTextContent_WithParametersOrUpperCase_IsSelectedLikeIsText()
    {
        using DatabaseFixture fixture = new();
        long json = await Seed(fixture, "", "application/json; charset=utf-8", Bytes(10));
        long upper = await Seed(fixture, "", "TEXT/Plain", Bytes(10));
        long spaced = await Seed(fixture, "", "Application/JSON ; charset=utf-8", Bytes(10));
        await Seed(fixture, "", "application/jsonx", Bytes(10));
        await Seed(fixture, "", "application/octet-stream", Bytes(10));
        EmbeddingBackfillService backfill = MakeRecordingBackfill(fixture);

        await backfill.RunAsync();

        Assert.That(await EmbeddedIds(fixture), Is.EqualTo(new[] { json, upper, spaced }));
    }

    [Test, Parallelizable]
    public async Task RowChangedAfterLoad_IsNotOverwritten()
    {
        using DatabaseFixture fixture = new();
        long changed = await Seed(fixture, "changed", "text/plain", Bytes(10));
        long unchanged = await Seed(fixture, "unchanged", "text/plain", Bytes(10));
        ListLogger<EmbeddingBackfillService> logger = new();
        EmbeddingBackfillService backfill = MakeRecordingBackfill(fixture, async () => {
            await fixture.EntityManager.Update<Node>()
                                       .Set(n => n.LastUpdate == DateTime.UtcNow.AddMinutes(5))
                                       .Where(n => n.Id == changed)
                                       .ExecuteAsync();
        }, logger);

        await backfill.RunAsync();

        Assert.That(await EmbeddedIds(fixture), Is.EqualTo(new[] { unchanged }));
        Assert.That(logger.Messages, Has.Some.Contains("event=backfill.complete embedded=1 skipped_changed=1 total=2"));
    }

    [Test, Parallelizable]
    public async Task RunAsync_UnchangedRows_LogsEmbeddedSkippedChangedTotalAndElapsed()
    {
        using DatabaseFixture fixture = new();
        await Seed(fixture, "one", null, null);
        await Seed(fixture, "two", null, null);
        ListLogger<EmbeddingBackfillService> logger = new();
        EmbeddingBackfillService backfill = MakeRecordingBackfill(fixture, logger: logger);

        await backfill.RunAsync();

        Assert.That(logger.Messages, Has.Some.Matches<string>(m => m.StartsWith("event=backfill.complete embedded=2 skipped_changed=0 total=2 elapsed=")));
    }
}
