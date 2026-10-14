using Backend.Init;
using Backend.Models;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Errors;
using Pooshit.Ocelot.Info;

namespace Backend.tests.Tests;

/// <summary>
/// tests for <see cref="EmbeddingModelGuard"/>, its startup run point and its backfill run point; all on SQLite
/// </summary>
[TestFixture, Parallelizable]
public class EmbeddingModelGuardTests
{
    const string Gemini = "gemini-embedding-001";
    const string Gemma = "embeddinggemma-2";

    static readonly IEmbeddingCapability Enabled = new EmbeddingCapability(true);
    static readonly IEmbeddingCapability Disabled = new EmbeddingCapability(false);

    static EmbeddingModelGuard MakeGuard(IEntityManager database, IEmbeddingCapability? capability = null, ILogger<EmbeddingModelGuard>? logger = null)
        => new(database, capability ?? Enabled, logger ?? NullLogger<EmbeddingModelGuard>.Instance);

    static async Task<string[]> RecordedModels(IEntityManager database)
    {
        List<string> models = [];
        await foreach (Metadata row in database.Load<Metadata>().Where(m => m.Name == MetadataKeys.EmbeddingModel).ExecuteEntitiesAsync())
            models.Add(row.Value);
        return models.ToArray();
    }

    static Task Record(IEntityManager database, string model)
        => database.Insert<Metadata>().Columns(m => m.Name, m => m.Value).Values(MetadataKeys.EmbeddingModel, model).ExecuteAsync();

    static async Task<long> SeedNodeWithVector(DatabaseFixture fixture)
    {
        NodeService service = new(fixture.EntityManager, Disabled);
        NodeDetails node = await service.CreateNode(new NodeDetails { Type = "doc", Name = "vectored" }, callerId: 0);
        await fixture.EntityManager.DBClient.NonQueryAsync("UPDATE node SET embedding = x'00000000' WHERE id = @1", node.Id);
        return node.Id;
    }

    [Test, Parallelizable]
    [Description("an empty database records the configured model")]
    public async Task Absent_NoVectors_InsertsConfigured()
    {
        using DatabaseFixture fixture = new();

        await MakeGuard(fixture.EntityManager).EnsureAsync(Gemma);

        Assert.That(await RecordedModels(fixture.EntityManager), Is.EqualTo(new[] { Gemma }));
    }

    [Test, Parallelizable]
    [Description("an unrecorded database whose nodes all lack vectors is empty of vectors: any model is recorded")]
    public async Task Absent_NodesWithoutVectors_LocalProducer_InsertsConfigured()
    {
        using DatabaseFixture fixture = new();
        NodeService service = new(fixture.EntityManager, Disabled);
        await service.CreateNode(new NodeDetails { Type = "doc", Name = "unembedded" }, callerId: 0);

        Assert.DoesNotThrowAsync(() => MakeGuard(fixture.EntityManager).EnsureAsync(Gemma));

        Assert.That(await RecordedModels(fixture.EntityManager), Is.EqualTo(new[] { Gemma }));
    }

    [Test, Parallelizable]
    [Description("the log line names the action taken: inserted for a new row, matched for an existing one")]
    public async Task Ensure_LogsActionTaken()
    {
        using DatabaseFixture fixture = new();
        ListLogger<EmbeddingModelGuard> logger = new();
        EmbeddingModelGuard guard = MakeGuard(fixture.EntityManager, logger: logger);

        await guard.EnsureAsync(Gemma);
        await guard.EnsureAsync(Gemma);

        Assert.That(logger.Messages, Is.EqualTo(new[] { $"event=embedding.model action=inserted model={Gemma}", $"event=embedding.model action=matched model={Gemma}" }));
    }

    [Test, Parallelizable]
    [Description("a refusal logs exactly the message it throws")]
    public async Task Refusal_LogsThrownMessage()
    {
        using DatabaseFixture fixture = new();
        await Record(fixture.EntityManager, Gemini);
        ListLogger<EmbeddingModelGuard> logger = new();

        InvalidOperationException? refusal = Assert.ThrowsAsync<InvalidOperationException>(() => MakeGuard(fixture.EntityManager, logger: logger).EnsureAsync(Gemma));

        Assert.That(logger.Messages, Is.EqualTo(new[] { refusal!.Message }));
    }

    [Test, Parallelizable]
    [Description("an unrecorded database with vectors belongs to the gemini model and records it when gemini is configured")]
    public async Task Absent_Vectors_FunctionProducer_InsertsGemini()
    {
        using DatabaseFixture fixture = new();
        await SeedNodeWithVector(fixture);

        await MakeGuard(fixture.EntityManager).EnsureAsync(Gemini);

        Assert.That(await RecordedModels(fixture.EntityManager), Is.EqualTo(new[] { Gemini }));
    }

    [Test, Parallelizable]
    [Description("an unrecorded database with vectors is refused for any other model and stays unrecorded")]
    public async Task Absent_Vectors_LocalProducer_Refuses()
    {
        using DatabaseFixture fixture = new();
        await SeedNodeWithVector(fixture);

        InvalidOperationException? refusal = Assert.ThrowsAsync<InvalidOperationException>(() => MakeGuard(fixture.EntityManager).EnsureAsync(Gemma));

        Assert.Multiple(async () => {
            Assert.That(refusal!.Message, Does.Contain($"unrecorded {Gemini} vectors").And.Contain(Gemma));
            Assert.That(await RecordedModels(fixture.EntityManager), Is.Empty);
        });
    }

    [Test, Parallelizable]
    [Description("a recorded model equal to the configured one continues, with or without vectors, and leaves the row alone")]
    public async Task Recorded_Match_Continues([Values(false, true)] bool hasVectors)
    {
        using DatabaseFixture fixture = new();
        await Record(fixture.EntityManager, Gemma);
        if (hasVectors)
            await SeedNodeWithVector(fixture);

        Assert.DoesNotThrowAsync(() => MakeGuard(fixture.EntityManager).EnsureAsync(Gemma));

        Assert.That(await RecordedModels(fixture.EntityManager), Is.EqualTo(new[] { Gemma }));
    }

    [Test, Parallelizable]
    [Description("a recorded model different from the configured one is refused, with or without vectors, and the row is kept")]
    public async Task Recorded_Mismatch_Refuses([Values(false, true)] bool hasVectors)
    {
        using DatabaseFixture fixture = new();
        await Record(fixture.EntityManager, Gemini);
        if (hasVectors)
            await SeedNodeWithVector(fixture);

        InvalidOperationException? refusal = Assert.ThrowsAsync<InvalidOperationException>(() => MakeGuard(fixture.EntityManager).EnsureAsync(Gemma));

        Assert.Multiple(async () => {
            Assert.That(refusal!.Message, Does.Contain($"holds {Gemini}").And.Contain($"writes {Gemma}").And.Contain("backfill-embeddings"));
            Assert.That(await RecordedModels(fixture.EntityManager), Is.EqualTo(new[] { Gemini }));
        });
    }

    [Test, Parallelizable]
    [Description("a competing instance recording the same model between read and insert is not fatal")]
    public async Task InsertConflict_RereadsAndContinues()
    {
        using DatabaseFixture fixture = new();
        IEntityManager racing = new EntityManager(MetadataInsertRaceClient.Wrap(fixture.EntityManager.DBClient, () => Record(fixture.EntityManager, Gemma)));

        Assert.DoesNotThrowAsync(() => MakeGuard(racing).EnsureAsync(Gemma));

        Assert.That(await RecordedModels(fixture.EntityManager), Is.EqualTo(new[] { Gemma }));
    }

    [Test, Parallelizable]
    [Description("a competing instance recording another model between read and insert is refused instead of overwritten")]
    public async Task InsertConflict_OtherModel_Refuses()
    {
        using DatabaseFixture fixture = new();
        IEntityManager racing = new EntityManager(MetadataInsertRaceClient.Wrap(fixture.EntityManager.DBClient, () => Record(fixture.EntityManager, Gemini)));

        InvalidOperationException? refusal = Assert.ThrowsAsync<InvalidOperationException>(() => MakeGuard(racing).EnsureAsync(Gemma));

        Assert.Multiple(async () => {
            Assert.That(refusal!.Message, Does.Contain($"holds {Gemini}"));
            Assert.That(await RecordedModels(fixture.EntityManager), Is.EqualTo(new[] { Gemini }));
        });
    }

    [Test, Parallelizable]
    [Description("an insert failing for a reason other than a competing row surfaces unchanged")]
    public void Insert_FailsWithoutCompetitor_Throws()
    {
        using DatabaseFixture fixture = new();
        IEntityManager failing = new EntityManager(MetadataInsertRaceClient.Wrap(fixture.EntityManager.DBClient, () => throw new SqliteException("boom", 1)));

        SqliteException? failure = Assert.ThrowsAsync<SqliteException>(() => MakeGuard(failing).EnsureAsync(Gemma));

        Assert.That(failure!.Message, Does.Contain("boom"));
    }

    [Test, Parallelizable]
    [Description("the embedding model key keeps the name the re-embed procedure deletes")]
    public void EmbeddingModelKey_IsEmbeddingModel()
    {
        Assert.That(MetadataKeys.EmbeddingModel, Is.EqualTo("embedding.model"));
    }

    [Test, Parallelizable]
    [Description("a metadata name is unique")]
    public async Task Metadata_DuplicateName_IsRejected()
    {
        using DatabaseFixture fixture = new();
        await Record(fixture.EntityManager, Gemma);

        Assert.ThrowsAsync<StatementException>(() => Record(fixture.EntityManager, Gemini));
    }

    [Test, Parallelizable]
    [Description("while the embedding capability is disabled the guard neither records nor refuses")]
    public async Task CapabilityDisabled_RecordsNothingAndNeverRefuses()
    {
        using DatabaseFixture fixture = new();
        EmbeddingModelGuard guard = MakeGuard(fixture.EntityManager, Disabled);

        await guard.EnsureAsync(Gemma);
        bool emptyAfterFirst = (await RecordedModels(fixture.EntityManager)).Length == 0;
        await Record(fixture.EntityManager, Gemini);
        Assert.DoesNotThrowAsync(() => guard.EnsureAsync(Gemma));

        Assert.That(emptyAfterFirst, Is.True);
    }

    [Test, Parallelizable]
    [Description("the backfill records the model of an unrecorded database before writing")]
    public async Task Backfill_FreshDatabase_RecordsModel()
    {
        using DatabaseFixture fixture = new();
        EmbeddingBackfillService backfill = MakeBackfill(fixture);

        await backfill.RunAsync();

        Assert.That(await RecordedModels(fixture.EntityManager), Is.EqualTo(new[] { TextContentTypePredicate.EmbeddingModel }));
    }

    [Test, Parallelizable]
    [Description("the backfill refuses a database recorded with another model before any node is written")]
    public async Task Backfill_Mismatch_RefusesBeforeAnyWrite()
    {
        using DatabaseFixture fixture = new();
        NodeService service = new(fixture.EntityManager, Disabled);
        NodeDetails node = await service.CreateNode(new NodeDetails { Type = "doc", Name = "candidate" }, callerId: 0);
        await Record(fixture.EntityManager, Gemma);
        EmbeddingBackfillService backfill = MakeBackfill(fixture);

        InvalidOperationException? refusal = Assert.ThrowsAsync<InvalidOperationException>(() => backfill.RunAsync());

        Node raw = await fixture.EntityManager.Load<Node>(n => n.Substance).Where(n => n.Id == node.Id).ExecuteEntityAsync();
        Assert.Multiple(() => {
            Assert.That(refusal!.Message, Does.Contain($"holds {Gemma}"));
            Assert.That(raw.Substance, Is.Not.EqualTo("embedded"), "the marker write of the backfill must not have run");
        });
    }

    static EmbeddingBackfillService MakeBackfill(DatabaseFixture fixture)
    {
        IEntityManager recording = new EntityManager(EmbeddingWriteMarkerClient.Wrap(fixture.EntityManager.DBClient));
        return new EmbeddingBackfillService(recording, Enabled, MakeGuard(recording), NullLogger<EmbeddingBackfillService>.Instance);
    }

    [Test, Parallelizable]
    [Description("host startup creates the schema first and then records the configured model")]
    public async Task Host_Startup_RecordsModelAfterSchema()
    {
        await using WebApplicationFactory<Program> factory = TestSetup.CreateTestFactory(databasePath: TempDatabase(), configureServices: Enable);

        IEntityManager database = factory.Services.GetRequiredService<IEntityManager>();

        Assert.That(await RecordedModels(database), Is.EqualTo(new[] { TextContentTypePredicate.EmbeddingModel }));
    }

    [Test, Parallelizable]
    [Description("host startup refuses to start on a database recorded with another model")]
    public async Task Host_Startup_RecordedMismatch_RefusesToStart()
    {
        string path = TempDatabase();
        await using (SqliteConnection connection = new($"Data Source={path}")) {
            await connection.OpenAsync();
            IEntityManager seed = new EntityManager(ClientFactory.Create(connection, new SQLiteInfo()));
            await new DatabaseModelService(seed).StartAsync(CancellationToken.None);
            await Record(seed, Gemma);
        }

        await using WebApplicationFactory<Program> factory = TestSetup.CreateTestFactory(databasePath: path, configureServices: Enable);

        Exception? failure = Assert.Catch(() => _ = factory.Services);

        Assert.That(failure!.ToString(), Does.Contain($"holds {Gemma}"));
    }

    static void Enable(IServiceCollection services) => services.AddSingleton(Enabled);

    static string TempDatabase() => Path.Combine(Path.GetTempPath(), $"guard-host-{Guid.NewGuid():N}.db3");
}
