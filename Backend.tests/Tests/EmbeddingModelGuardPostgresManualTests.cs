using Backend.Models;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Pooshit.Ocelot.Entities;

namespace Backend.tests.Tests;

/// <summary>
/// manual-lane Postgres test of the embedding model guard against a real metadata table and a real vector column
/// </summary>
[TestFixture, Parallelizable]
public class EmbeddingModelGuardPostgresManualTests
{
    const string Gemini = "gemini-embedding-001";
    const string Gemma = "embeddinggemma-2";

    static EmbeddingModelGuard MakeGuard(IEntityManager database)
        => new(database, new EmbeddingCapability(true), NullLogger<EmbeddingModelGuard>.Instance);

    static async Task<string[]> RecordedModels(IEntityManager database)
    {
        List<string> models = [];
        await foreach (Metadata row in database.Load<Metadata>().Where(m => m.Name == MetadataKeys.EmbeddingModel).ExecuteEntitiesAsync())
            models.Add(row.Value);
        return models.ToArray();
    }

    [Test, Parallelizable]
    [Explicit("Manual Postgres test - requires POSTGRES_CONNECTION to a superuser connection with the pgvector extension available.")]
    [Category("PostgresManual")]
    [Description("the metadata table is created with lower-case name and value columns")]
    public async Task Schema_CreatesMetadataTableWithNameAndValueColumns()
    {
        using EmbeddingPostgresDatabase database = new();
        await database.Start();

        Assert.That(database.Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_name = 'metadata' AND column_name IN ('name', 'value')"), Is.EqualTo(2));
    }

    [Test, Parallelizable]
    [Explicit("Manual Postgres test - requires POSTGRES_CONNECTION to a superuser connection with the pgvector extension available.")]
    [Category("PostgresManual")]
    [Description("a real vector column counts as vectors: gemini is recorded for it, any other model is refused")]
    public async Task UnrecordedVectors_AreGemini()
    {
        using EmbeddingPostgresDatabase database = new();
        await database.Start();
        NodeService service = new(database.Entities, new EmbeddingCapability(false), EmbeddingProducers.Function);
        NodeDetails node = await service.CreateNode(new NodeDetails { Type = "doc", Name = "vectored" }, callerId: 0);
        database.Scalar<object?>($"UPDATE node SET embedding = array_fill(0.1::real, ARRAY[768])::vector WHERE id = {node.Id} RETURNING 1");

        InvalidOperationException? refusal = Assert.ThrowsAsync<InvalidOperationException>(() => MakeGuard(database.Entities).EnsureAsync(Gemma));
        string[] afterRefusal = await RecordedModels(database.Entities);
        await MakeGuard(database.Entities).EnsureAsync(Gemini);

        Assert.Multiple(async () => {
            Assert.That(refusal!.Message, Does.Contain($"unrecorded {Gemini} vectors"));
            Assert.That(afterRefusal, Is.Empty);
            Assert.That(await RecordedModels(database.Entities), Is.EqualTo(new[] { Gemini }));
        });
    }

    [Test, Parallelizable]
    [Explicit("Manual Postgres test - requires POSTGRES_CONNECTION to a superuser connection with the pgvector extension available.")]
    [Category("PostgresManual")]
    [Description("nodes whose vector column is NULL do not count as vectors on a real vector column")]
    public async Task NodesWithoutVectors_AreNotVectors()
    {
        using EmbeddingPostgresDatabase database = new();
        await database.Start();
        NodeService service = new(database.Entities, new EmbeddingCapability(false), EmbeddingProducers.Function);
        await service.CreateNode(new NodeDetails { Type = "doc", Name = "unembedded" }, callerId: 0);

        Assert.DoesNotThrowAsync(() => MakeGuard(database.Entities).EnsureAsync(Gemma));

        Assert.That(await RecordedModels(database.Entities), Is.EqualTo(new[] { Gemma }));
    }

    [Test, Parallelizable]
    [Explicit("Manual Postgres test - requires POSTGRES_CONNECTION to a superuser connection with the pgvector extension available.")]
    [Category("PostgresManual")]
    [Description("two instances with the same model racing on a fresh database both continue and leave one row")]
    public async Task ConcurrentFirstStart_SameModel_BothContinue()
    {
        using EmbeddingPostgresDatabase database = new();
        await database.Start();

        for (int round = 0; round < 20; round++) {
            await database.Entities.Delete<Metadata>().ExecuteAsync();
            Assert.DoesNotThrowAsync(() => Task.WhenAll(MakeGuard(database.Entities).EnsureAsync(Gemma), MakeGuard(database.Entities).EnsureAsync(Gemma)));
            Assert.That(await RecordedModels(database.Entities), Is.EqualTo(new[] { Gemma }));
        }
    }

    [Test, Parallelizable]
    [Explicit("Manual Postgres test - requires POSTGRES_CONNECTION to a superuser connection with the pgvector extension available.")]
    [Category("PostgresManual")]
    [Description("two instances with different models racing on a fresh database end with exactly one refusal and the winner's row")]
    public async Task ConcurrentFirstStart_DifferentModels_ExactlyOneRefuses()
    {
        using EmbeddingPostgresDatabase database = new();
        await database.Start();

        for (int round = 0; round < 20; round++) {
            await database.Entities.Delete<Metadata>().ExecuteAsync();
            string[] models = [Gemma, Gemini];
            Task[] starts = [MakeGuard(database.Entities).EnsureAsync(models[0]), MakeGuard(database.Entities).EnsureAsync(models[1])];
            try {
                await Task.WhenAll(starts);
            } catch (InvalidOperationException) {
            }

            string[] recorded = await RecordedModels(database.Entities);
            Assert.Multiple(() => {
                Assert.That(starts.Count(t => t.IsFaulted), Is.EqualTo(1));
                Assert.That(recorded, Is.EqualTo(new[] { models[Array.FindIndex(starts, t => !t.IsFaulted)] }));
            });
        }
    }
}
