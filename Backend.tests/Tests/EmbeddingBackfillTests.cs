using System.Text;
using System.Text.RegularExpressions;
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
///
/// disabled-provider tests: verify the no-op path when IsEnabled=false.
/// enabled-provider (GoogleMl) tests on SQLite: verify selection predicates and
///   prove that embedding() is attempted for qualifying nodes by observing the
///   expected SQLite failure (embedding() does not exist on SQLite).
/// </summary>
[TestFixture]
public class EmbeddingBackfillTests
{
    static readonly IEmbeddingProvider EnabledProvider
        = new GoogleMlEmbeddingProvider(TextContentTypePredicate.EmbeddingModel);

    static NodeService MakeNodeService(DatabaseFixture fixture)
        => new(fixture.EntityManager, NullEmbeddingProvider.Instance);

    static EmbeddingBackfillService MakeBackfill(DatabaseFixture fixture, IEmbeddingProvider provider)
        => new(fixture.EntityManager, provider, NullLogger<EmbeddingBackfillService>.Instance);

    [Test]
    public async Task RunAsync_CapabilityDisabled_ExitsWithoutWriting()
    {
        using DatabaseFixture fixture = new();
        NodeService nodeSvc = MakeNodeService(fixture);
        EmbeddingBackfillService backfill = MakeBackfill(fixture, NullEmbeddingProvider.Instance);

        NodeDetails node = await nodeSvc.CreateNode(new NodeDetails { Type = "doc", Name = "ShouldNotBeEmbedded" }, callerId: 0);
        byte[] content = Encoding.UTF8.GetBytes("some markdown text");
        await nodeSvc.UploadContent(node.Id, "text/markdown", new MemoryStream(content), callerId: 0, isAdmin: true);

        await backfill.RunAsync();

        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == node.Id)
                                              .ExecuteEntityAsync();

        Assert.That(raw.Embedding, Is.Null,
            "Embedding must remain null — provider was disabled so RunAsync must be a no-op");
    }

    [Test]
    public async Task RunAsync_CapabilityEnabled_SkipsNonTextNodesWithoutName()
    {
        using DatabaseFixture fixture = new();
        NodeService nodeSvc = MakeNodeService(fixture);

        NodeDetails node = await nodeSvc.CreateNode(new NodeDetails { Type = "asset", Name = "" }, callerId: 0);
        byte[] content = [0x89, 0x50, 0x4E, 0x47];
        await nodeSvc.UploadContent(node.Id, "image/png", new MemoryStream(content), callerId: 0, isAdmin: true);

        EmbeddingBackfillService backfill = MakeBackfill(fixture, EnabledProvider);

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
        using DatabaseFixture fixture = new();
        NodeService nodeSvc = MakeNodeService(fixture);

        NodeDetails node = await nodeSvc.CreateNode(new NodeDetails { Type = "asset", Name = "PngWithName" }, callerId: 0);
        byte[] content = [0x89, 0x50, 0x4E, 0x47];
        await nodeSvc.UploadContent(node.Id, "image/png", new MemoryStream(content), callerId: 0, isAdmin: true);

        EmbeddingBackfillService backfill = MakeBackfill(fixture, EnabledProvider);

        Assert.CatchAsync(() => backfill.RunAsync(),
            "node with non-empty name is a v2 candidate; RunAsync must attempt (and fail on SQLite) to embed it");
    }

    [Test]
    public async Task RunAsync_CapabilityEnabled_SkipsNodesWithNullContentAndEmptyName()
    {
        using DatabaseFixture fixture = new();
        NodeService nodeSvc = MakeNodeService(fixture);

        long nodeId = await fixture.EntityManager.Insert<Node>()
                                                 .Columns(n => n.Name, n => n.TypeId)
                                                 .Values("", 0)
                                                 .ReturnID()
                                                 .ExecuteAsync();

        EmbeddingBackfillService backfill = MakeBackfill(fixture, EnabledProvider);

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
        using DatabaseFixture fixture = new();
        NodeService nodeSvc = MakeNodeService(fixture);

        NodeDetails node = await nodeSvc.CreateNode(new NodeDetails { Type = "doc", Name = "NameNoContent" }, callerId: 0);

        EmbeddingBackfillService backfill = MakeBackfill(fixture, EnabledProvider);

        Assert.CatchAsync(() => backfill.RunAsync(),
            "node with non-empty name and null content is a v2 candidate; RunAsync must attempt (and fail on SQLite) to embed it");
    }

    [Test]
    public async Task RunAsync_CapabilityDisabled_EmptyDb_CompletesCleanly()
    {
        using DatabaseFixture fixture = new();
        EmbeddingBackfillService backfill = MakeBackfill(fixture, NullEmbeddingProvider.Instance);

        Assert.DoesNotThrowAsync(() => backfill.RunAsync());
    }

    [Test]
    public async Task CandidatePredicate_SqlShape_NameArmOrContentArm()
    {
        using DatabaseFixture fixture = new();

        string commandText = fixture.EntityManager
                                    .Load<Node>(n => n.Id)
                                    .Where(EmbeddingBackfillService.CandidatePredicate().Content)
                                    .Prepare()
                                    .CommandText;

        string where = Regex.Match(commandText, @"(?i)\bWHERE\b(.+)$").Groups[1].Value.Trim();

        Assert.That(where, Does.Contain("OR").IgnoreCase,
            "candidate predicate must use OR to join the name arm and the text-content arm");
    }
}
