using System.Data;
using System.Data.Common;
using System.Text;
using Backend.Extensions.Startup;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Info;

namespace Backend.tests.Tests;

/// <summary>
/// pins that every embedding write and query asks the configured <see cref="IEmbeddingProducer"/> for its vectors
/// </summary>
[TestFixture, Parallelizable]
public class EmbeddingProducerSeamTests
{
    static DataTable NameTable(string name)
    {
        DataTable table = new();
        table.Columns.Add("name", typeof(string));
        table.Rows.Add(name);
        return table;
    }

    static DataTable NodeRow()
    {
        DataTable table = new();
        table.Columns.Add("id", typeof(long));
        table.Columns.Add("type", typeof(string));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("status", typeof(string));
        table.Columns.Add("severity", typeof(int));
        table.Columns.Add("refinement", typeof(string));
        table.Columns.Add("rootnodeid", typeof(long));
        table.Columns.Add("contenttype", typeof(string));
        table.Columns.Add("x", typeof(double));
        table.Columns.Add("y", typeof(double));
        table.Columns.Add("content", typeof(byte[]));
        table.Columns.Add("substance", typeof(string));
        table.Columns.Add("ownerid", typeof(long));
        table.Columns.Add("access", typeof(int));
        table.Columns.Add("created", typeof(DateTime));
        table.Columns.Add("lastupdate", typeof(DateTime));
        table.Rows.Add(5L, "task", "named", DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, 0.0, 0.0, DBNull.Value, DBNull.Value, 0L, 3, DateTime.UtcNow, DateTime.UtcNow);
        return table;
    }

    static DataTable NodeFields(string name, string contentType, byte[] content)
    {
        DataTable table = new();
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("contenttype", typeof(string));
        table.Columns.Add("content", typeof(byte[]));
        table.Rows.Add(name, contentType, content);
        return table;
    }

    static DataTable CandidateTable()
    {
        DataTable table = new();
        table.Columns.Add("id", typeof(long));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("contenttype", typeof(string));
        table.Columns.Add("content", typeof(byte[]));
        table.Columns.Add("lastupdate", typeof(DateTime));
        table.Rows.Add(5L, "named", "text/markdown", Encoding.UTF8.GetBytes("body"), DateTime.UtcNow);
        return table;
    }

    static Reader ReaderOf(DataTable table) => new(new DataTableReader(table), null!, new PostgreInfo());

    static Mock<IDBClient> CreateClient(Func<string, DataTable> rows)
    {
        Mock<IDBClient> client = new() { DefaultValueProvider = new CompletedTaskDefaults() };
        client.SetupGet(c => c.DBInfo).Returns(new PostgreInfo());
        client.Setup(c => c.Transaction()).Returns(() => {
            Mock<DbConnection> connection = new();
            connection.Protected().Setup<DbTransaction>("BeginDbTransaction", ItExpr.IsAny<IsolationLevel>()).Returns(new Mock<DbTransaction>().Object);
            return ClientFactory.Create(connection.Object, new PostgreInfo()).Transaction();
        });
        client.Setup(c => c.ReaderAsync(It.IsAny<Transaction>(), It.IsAny<string>(), It.IsAny<IEnumerable<object>>(), It.IsAny<CancellationToken>())).ReturnsAsync((Transaction _, string sql, IEnumerable<object> _, CancellationToken _) => ReaderOf(rows(sql)));
        client.Setup(c => c.ReaderAsync(It.IsAny<Transaction>(), It.IsAny<string>(), It.IsAny<IEnumerable<object>>())).ReturnsAsync((Transaction _, string sql, IEnumerable<object> _) => ReaderOf(rows(sql)));
        client.Setup(c => c.ReaderAsync(It.IsAny<string>(), It.IsAny<IEnumerable<object>>())).ReturnsAsync((string sql, IEnumerable<object> _) => ReaderOf(rows(sql)));
        client.Setup(c => c.ReaderAsync(It.IsAny<string>(), It.IsAny<object[]>())).ReturnsAsync((string sql, object[] _) => ReaderOf(rows(sql)));
        return client;
    }

    static NodeService ServiceThrowingOnRead(RecordingEmbeddingProducer producer)
    {
        Mock<IDBClient> client = new();
        client.SetupGet(c => c.DBInfo).Returns(new PostgreInfo());
        client.Setup(c => c.ReaderAsync(It.IsAny<Transaction>(), It.IsAny<string>(), It.IsAny<IEnumerable<object>>(), It.IsAny<CancellationToken>())).Throws<QueryExecuted>();
        return new(new EntityManager(client.Object), new EmbeddingCapability(true), producer);
    }

    [Test, Parallelizable]
    [Description("DiVoid #17524 unit 1: the window combine asks the producer once per window, for a document vector")]
    public void OfTexts_AsksProducerOncePerWindowAsDocument()
    {
        RecordingEmbeddingProducer producer = new();

        EmbeddingExpression.OfTexts(["first", "second", "third"], producer);

        Assert.That(producer.Calls, Is.EqualTo(new[] { ("first", EmbeddingPurpose.Document), ("second", EmbeddingPurpose.Document), ("third", EmbeddingPurpose.Document) }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #17524 unit 1: a content upload embeds the composed text through the producer")]
    public async Task UploadContent_EmbedsComposedTextThroughProducer()
    {
        RecordingEmbeddingProducer producer = new();
        Mock<IDBClient> client = CreateClient(sql => sql.Contains("INNER JOIN nodetype") ? NodeRow() : NameTable("named"));
        NodeService service = new(new EntityManager(client.Object), new EmbeddingCapability(true), producer);

        await service.UploadContent(5, "text/markdown", new MemoryStream(Encoding.UTF8.GetBytes("body")), callerId: 0, isAdmin: true);

        Assert.That(producer.Calls, Is.EqualTo(new[] { ("named\n\nbody", EmbeddingPurpose.Document) }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #17524 unit 1: a name patch re-embeds the node through the producer")]
    public async Task PatchName_ReEmbedsComposedTextThroughProducer()
    {
        RecordingEmbeddingProducer producer = new();
        Mock<IDBClient> client = CreateClient(sql => sql.Contains("INNER JOIN nodetype") ? NodeRow() : NodeFields("renamed", "text/plain", Encoding.UTF8.GetBytes("body")));
        NodeService service = new(new EntityManager(client.Object), new EmbeddingCapability(true), producer);

        await service.Patch(5, [new PatchOperation { Op = "replace", Path = "/name", Value = "renamed" }], callerId: 0, isAdmin: true, CancellationToken.None);

        Assert.That(producer.Calls, Is.EqualTo(new[] { ("renamed\n\nbody", EmbeddingPurpose.Document) }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #17524 unit 1: a node created with a name is embedded through the producer")]
    public async Task CreateNode_EmbedsNameThroughProducer()
    {
        RecordingEmbeddingProducer producer = new();
        Mock<IDBClient> client = CreateClient(sql => sql.Contains("INNER JOIN nodetype") ? NodeRow() : NameTable("named"));
        NodeService service = new(new EntityManager(client.Object), new EmbeddingCapability(true), producer);

        await service.CreateNode(new NodeDetails { Type = "task", Name = "named" }, callerId: 0);

        Assert.That(producer.Calls, Is.EqualTo(new[] { ("named", EmbeddingPurpose.Document) }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #17524 unit 1: the backfill embeds a candidate through the producer")]
    public async Task RunAsync_Candidate_EmbedsComposedTextThroughProducer()
    {
        RecordingEmbeddingProducer producer = new();
        Mock<IDBClient> client = CreateClient(_ => CandidateTable());
        EmbeddingBackfillService backfill = new(new EntityManager(client.Object), new EmbeddingCapability(true), new Mock<IEmbeddingModelGuard>().Object, NullLogger<EmbeddingBackfillService>.Instance, producer);

        await backfill.RunAsync();

        Assert.That(producer.Calls, Is.EqualTo(new[] { ("named\n\nbody", EmbeddingPurpose.Document) }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #17524 unit 4: the backfill prepares the producer once, after the model guard and before the first embedding")]
    public async Task RunAsync_Enabled_PreparesProducerAfterGuardAndBeforeEmbedding() {
        RecordingEmbeddingProducer producer = new();
        Mock<IDBClient> client = CreateClient(_ => CandidateTable());
        List<string> order = [];
        Mock<IEmbeddingModelGuard> guard = new();
        guard.Setup(g => g.EnsureAsync(It.IsAny<string>())).Callback(() => order.Add("guard")).Returns(Task.CompletedTask);
        EmbeddingBackfillService backfill = new(new EntityManager(client.Object), new EmbeddingCapability(true), guard.Object, NullLogger<EmbeddingBackfillService>.Instance, producer);

        await backfill.RunAsync();

        Assert.That(order.Concat(producer.Events), Is.EqualTo(new[] { "guard", "prepare", "embed" }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #17524 unit 4: a database the guard refuses is never used to prepare the producer, so no model is downloaded for it")]
    public void RunAsync_GuardRefuses_DoesNotPrepareProducer() {
        RecordingEmbeddingProducer producer = new();
        Mock<IEmbeddingModelGuard> guard = new();
        guard.Setup(g => g.EnsureAsync(It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("mismatch"));
        EmbeddingBackfillService backfill = new(new EntityManager(CreateClient(_ => CandidateTable()).Object), new EmbeddingCapability(true), guard.Object, NullLogger<EmbeddingBackfillService>.Instance, producer);

        Assert.ThrowsAsync<InvalidOperationException>(() => backfill.RunAsync());

        Assert.That(producer.Events, Is.Empty);
    }

    [Test, Parallelizable]
    [Description("DiVoid #17524 unit 1: every query of a multi-query search is embedded through the producer as a query vector")]
    public void ListPaged_MultiQuery_EmbedsEachQueryAsQuery()
    {
        RecordingEmbeddingProducer producer = new();
        NodeService service = ServiceThrowingOnRead(producer);

        Assert.ThrowsAsync<QueryExecuted>(() => service.ListPaged(new NodeFilter { Queries = ["alpha", "beta"] }, callerId: 0, isAdmin: true));

        Assert.That(producer.Calls, Is.EqualTo(new[] { ("alpha", EmbeddingPurpose.Query), ("beta", EmbeddingPurpose.Query) }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #17524 unit 1: a single-query search embeds the query through the producer as a query vector")]
    public void ListPaged_SingleQuery_EmbedsQueryAsQuery()
    {
        RecordingEmbeddingProducer producer = new();
        NodeService service = ServiceThrowingOnRead(producer);

        Assert.ThrowsAsync<QueryExecuted>(() => service.ListPaged(new NodeFilter { Query = "alpha" }, callerId: 0, isAdmin: true));

        Assert.That(producer.Calls, Is.EqualTo(new[] { ("alpha", EmbeddingPurpose.Query) }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #17524 unit 1: a path-mode search embeds its query through the producer as a query vector")]
    public void ListPagedByPath_Query_EmbedsQueryAsQuery()
    {
        RecordingEmbeddingProducer producer = new();
        NodeService service = ServiceThrowingOnRead(producer);

        Assert.ThrowsAsync<QueryExecuted>(() => service.ListPagedByPath(new NodePathFilter { Path = "[type:task]", Query = "alpha" }, callerId: 0, isAdmin: true, CancellationToken.None));

        Assert.That(producer.Calls, Is.EqualTo(new[] { ("alpha", EmbeddingPurpose.Query) }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #17524 unit 1: the Postgres function producer names the gemini model")]
    public void PostgresFunctionProducer_NamesGeminiModel()
    {
        Assert.That(EmbeddingProducers.Function.Model, Is.EqualTo("gemini-embedding-001"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #17524 unit 1: the shared registration helper registers the function producer and derives the capability from the database type")]
    [TestCase("Postgres", true)]
    [TestCase("Sqlite", false)]
    public void AddEmbedding_RegistersFunctionProducerAndCapability(string databaseType, bool enabled)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Type"] = databaseType, ["Embedding:Producer"] = "PostgresFunction" }).Build();
        using ServiceProvider provider = new ServiceCollection().AddEmbedding(configuration).BuildServiceProvider();

        Assert.Multiple(() => {
            Assert.That(provider.GetRequiredService<IEmbeddingProducer>(), Is.TypeOf<PostgresFunctionEmbeddingProducer>());
            Assert.That(provider.GetRequiredService<IEmbeddingCapability>().IsEnabled, Is.EqualTo(enabled));
        });
    }
}
