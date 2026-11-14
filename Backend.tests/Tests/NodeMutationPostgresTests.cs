using System.Text;
using System.Data;
using System.Linq.Expressions;
using Moq;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Backend.Models.Nodes;
using Backend.Models.Messages;
using Backend.Models.Users;
using Npgsql;
using Pooshit.Ocelot.Tokens;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.Json;

namespace Backend.tests.Tests;

/// <summary>PostgreSQL mutation completion and rollback guards.</summary>
[TestFixture, Parallelizable]
public class NodeMutationPostgresTests {
    static async Task<EmbeddingPostgresDatabase> CreateDatabase() {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("POSTGRES_CONNECTION"))) Assert.Ignore("POSTGRES_CONNECTION is required");
        EmbeddingPostgresDatabase database = new();
        await database.Start();
        return database;
    }

    /// <summary>Concurrent distinct-node upload completion.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test, Parallelizable]
    [Description("Overlapping writes to distinct nodes all commit content and its immediately searchable vector.")]
    public async Task UploadContent_ConcurrentDistinctNodes_AllBodiesAndSearchVectorsCommit() {
        using EmbeddingPostgresDatabase database = await CreateDatabase();
        NodeService setup = new(database.Entities, new EmbeddingCapability(true), new HashedWordEmbeddingProducer());
        string[] words = ["bicycle", "gardening", "astronomy"];
        List<NodeDetails> nodes = [];
        foreach (string word in words) nodes.Add(await setup.CreateNode(new NodeDetails { Name = word, Type = "documentation" }, 0));
        using BarrierEmbeddingProducer producer = new(3);
        NodeService service = new(database.Entities, new EmbeddingCapability(true), producer);
        byte[][] bodies = words.Select(word => Encoding.UTF8.GetBytes(string.Join(' ', Enumerable.Repeat(word, 220)))).ToArray();

        await Task.WhenAll(nodes.Select((node, index) => Task.Run(async () => {
            using MemoryStream content = new(bodies[index]);
            await service.UploadContent(node.Id, "text/plain", content, 0, true);
        })));

        Assert.That(producer.Calls, Is.GreaterThanOrEqualTo(3));
        for (int index = 0; index < nodes.Count; index++) {
            (string contentType, Stream content) = await service.GetNodeData(nodes[index].Id, 0, true);
            using (content) {
                using MemoryStream actual = new();
                await content.CopyToAsync(actual);
                Assert.That(actual.ToArray(), Is.EqualTo(bodies[index]));
            }
            using MemoryStream response = new();
            await (await service.ListPaged(new NodeFilter { Query = words[index], Count = 1 }, 0, true)).Write(response);
            Page<NodeDetails> page = Json.Read<Page<NodeDetails>>(Encoding.UTF8.GetString(response.ToArray()));
            Assert.That(page.Result.Single().Id, Is.EqualTo(nodes[index].Id));
            Assert.That(contentType, Is.EqualTo("text/plain"));
        }
    }

    /// <summary>Serialization retry selection and exhaustion.</summary>
    /// <param name="sqlState">Injected database error code.</param>
    /// <param name="failures">Number of injected failures.</param>
    /// <param name="expectedAttempts">Expected number of transaction attempts.</param>
    /// <param name="succeeds">Whether the upload should complete.</param>
    /// <param name="atCommit">Whether the failure occurs during commit.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase("40001", 1, 2, true, false), Parallelizable]
    [TestCase("40001", 1, 2, true, true)]
    [TestCase("40001", 100, 8, false, false)]
    [TestCase("40001", 100, 8, false, true)]
    [TestCase("23505", 1, 1, false, false)]
    [TestCase("08006", 1, 1, false, false)]
    [Description("Only serialization aborts replay, at most eight attempts, with failed writes rolled back.")]
    public async Task UploadContent_DatabaseFailure_RetriesOnlySerializationAbort(string sqlState, int failures, long expectedAttempts, bool succeeds, bool atCommit) {
        using EmbeddingPostgresDatabase database = await CreateDatabase();
        NodeService service = new(database.Entities, new EmbeddingCapability(false), EmbeddingProducers.Function);
        long nodeId = (await service.CreateNode(new NodeDetails { Name = "failure guard" }, 0)).Id;
        using MemoryStream initial = new("original"u8.ToArray());
        await service.UploadContent(nodeId, "text/plain", initial, 0, true);
        await database.Entities.DBClient.NonQueryAsync("CREATE SEQUENCE mutation_attempts");
        await database.Entities.DBClient.NonQueryAsync("CREATE TABLE failure_plan (sqlstate text, failures int)");
        database.Scalar<int>("INSERT INTO failure_plan VALUES (@code, @failures) RETURNING failures", ("code", sqlState), ("failures", failures));
        await database.Entities.DBClient.NonQueryAsync("""
            CREATE FUNCTION fail_content() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE plan failure_plan; BEGIN
            SELECT * INTO plan FROM failure_plan;
            IF nextval('mutation_attempts') <= plan.failures THEN RAISE EXCEPTION USING ERRCODE = plan.sqlstate, MESSAGE = 'injected database failure'; END IF;
            RETURN NEW; END $$
            """);
        await database.Entities.DBClient.NonQueryAsync(atCommit
            ? "CREATE CONSTRAINT TRIGGER fail_content AFTER UPDATE ON node DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.content IS DISTINCT FROM NEW.content) EXECUTE FUNCTION fail_content()"
            : "CREATE TRIGGER fail_content BEFORE UPDATE OF content ON node FOR EACH ROW EXECUTE FUNCTION fail_content()");
        using MemoryStream data = new("replacement"u8.ToArray());

        if (succeeds) await service.UploadContent(nodeId, "text/plain", data, 0, true);
        else {
            Exception exception = Assert.CatchAsync(() => service.UploadContent(nodeId, "text/plain", data, 0, true))!;
            while (exception is not PostgresException && exception.InnerException != null) exception = exception.InnerException;
            Assert.That(exception, Is.InstanceOf<PostgresException>());
            Assert.That(((PostgresException)exception).SqlState, Is.EqualTo(sqlState));
        }

        Assert.That(database.Scalar<long>("SELECT last_value FROM mutation_attempts"), Is.EqualTo(expectedAttempts));
        Assert.That(database.Scalar<byte[]>("SELECT content FROM node WHERE id = @id", ("id", nodeId)),
            Is.EqualTo(Encoding.UTF8.GetBytes(succeeds ? "replacement" : "original")));
    }

    /// <summary>Concurrent relative content edits and notifications.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test, Parallelizable]
    [Description("Concurrent relative content edits reread the committed body and send one notification per commit.")]
    public async Task PatchContent_ConcurrentSameNode_PreservesEveryEditAndOneNoticePerCommit() {
        using EmbeddingPostgresDatabase database = await CreateDatabase();
        NodeService service = new(database.Entities, new EmbeddingCapability(false), EmbeddingProducers.Function);
        long userId = await database.Entities.Insert<User>().Columns(u => u.Name, u => u.Enabled, u => u.CreatedAt)
            .Values("subscriber", true, DateTime.UtcNow).ReturnID().ExecuteAsync();
        long nodeId = (await service.CreateNode(new NodeDetails { Name = "shared body" }, userId)).Id;
        using MemoryStream initial = new("seed"u8.ToArray());
        await service.UploadContent(nodeId, "text/plain", initial, userId, false);
        await service.RegisterHook(new NodeHook { NodeId = nodeId, UserId = userId, Tag = "content", Events = NodeHookEvents.Content }, userId, false);

        await Task.WhenAll(new[] { "a", "b", "c" }.Select(value => Task.Run(() => service.PatchContent(nodeId,
            [new ContentEdit { Unit = ContentEditUnit.Char, Start = 0, Length = 0, Value = value }], userId, false, default))));

        string body = Encoding.UTF8.GetString(database.Scalar<byte[]>("SELECT content FROM node WHERE id = @id", ("id", nodeId)));
        Assert.That(body, Does.EndWith("seed"));
        Assert.That(body[..3].Order(), Is.EqualTo("abc"));
        Assert.That(await database.Entities.Load<Message>(DB.Count()).ExecuteScalarAsync<long>(), Is.EqualTo(3));
    }

    /// <summary>Notification rollback before transaction replay.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test, Parallelizable]
    [Description("An abort after inserting a first notification rolls back all effects before replay.")]
    public async Task UploadContent_NotificationSerializationAbort_RollsBackEarlierNoticeBeforeRetry() {
        using EmbeddingPostgresDatabase database = await CreateDatabase();
        NodeService service = new(database.Entities, new EmbeddingCapability(false), EmbeddingProducers.Function);
        long userId = await database.Entities.Insert<User>().Columns(u => u.Name, u => u.Enabled, u => u.CreatedAt)
            .Values("subscriber", true, DateTime.UtcNow).ReturnID().ExecuteAsync();
        long nodeId = (await service.CreateNode(new NodeDetails { Name = "notification guard" }, userId)).Id;
        foreach (string tag in new[] { "first", "second" })
            await service.RegisterHook(new NodeHook { NodeId = nodeId, UserId = userId, Tag = tag }, userId, false);
        await database.Entities.DBClient.NonQueryAsync("CREATE SEQUENCE notice_attempts");
        await database.Entities.DBClient.NonQueryAsync("""
            CREATE FUNCTION fail_second_notice() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
            IF nextval('notice_attempts') = 2 THEN RAISE EXCEPTION USING ERRCODE = '40001', MESSAGE = 'injected notice serialization abort'; END IF;
            RETURN NEW; END $$
            """);
        await database.Entities.DBClient.NonQueryAsync("CREATE TRIGGER fail_second_notice BEFORE INSERT ON message FOR EACH ROW EXECUTE FUNCTION fail_second_notice()");
        using MemoryStream data = new("committed body"u8.ToArray());

        await service.UploadContent(nodeId, "text/plain", data, userId, false);

        Assert.That(database.Scalar<long>("SELECT last_value FROM notice_attempts"), Is.EqualTo(4));
        Assert.That(await database.Entities.Load<Message>(DB.Count()).ExecuteScalarAsync<long>(), Is.EqualTo(2));
        Assert.That(database.Scalar<byte[]>("SELECT content FROM node WHERE id = @id", ("id", nodeId)), Is.EqualTo("committed body"u8.ToArray()));
    }

    /// <summary>Content preservation after cancellation.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test, Parallelizable]
    [Description("A cancelled mutation does not change the stored content.")]
    public async Task UploadContent_CancelledToken_LeavesContentUnchanged() {
        using EmbeddingPostgresDatabase database = await CreateDatabase();
        NodeService service = new(database.Entities, new EmbeddingCapability(false), EmbeddingProducers.Function);
        long nodeId = (await service.CreateNode(new NodeDetails { Name = "cancel guard" }, 0)).Id;
        using MemoryStream data = new("cancelled"u8.ToArray());
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(() => service.UploadContent(nodeId, "text/plain", data, 0, true, cancellation.Token));

        Assert.That(database.Scalar<bool>("SELECT content IS NULL FROM node WHERE id = @id", ("id", nodeId)), Is.True);
    }

    /// <summary>Completed transaction cleanup preserves the original failure and closes its connection.</summary>
    /// <param name="committed">Whether the transaction committed before the failure.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    [Description("A completed transaction closes its connection and preserves the primary failure without replay.")]
    public async Task UploadContent_CompletedTransactionFailure_ClosesConnectionAndPreservesPrimary(bool committed) {
        using EmbeddingPostgresDatabase database = await CreateDatabase();
        NodeService setup = new(database.Entities, new EmbeddingCapability(false), EmbeddingProducers.Function);
        long nodeId = (await setup.CreateNode(new NodeDetails { Name = "cleanup guard" }, 0)).Id;
        Transaction? transaction = null;
        NpgsqlConnection? connection = null;
        Mock<IEntityManager> entities = new();
        entities.SetupGet(manager => manager.DBClient).Returns(database.Entities.DBClient);
        entities.Setup(manager => manager.Transaction()).Returns(() => {
            transaction = database.Entities.Transaction();
            connection = (NpgsqlConnection)transaction.DbTransaction.Connection!;
            return transaction;
        });
        entities.Setup(manager => manager.Update<Node>()).Returns(() => database.Entities.Update<Node>());
        entities.Setup(manager => manager.Load<Node>(It.IsAny<Expression<Func<Node, object>>[]>()))
            .Returns((Expression<Func<Node, object>>[] fields) => database.Entities.Load<Node>(fields));
        Exception primary = committed
            ? new NpgsqlException("simulated lost commit acknowledgement", new IOException("simulated transport failure"))
            : new InvalidOperationException("original operation failure");
        Mock<IEmbeddingProducer> producer = new();
        producer.Setup(value => value.Embed(It.IsAny<string>(), EmbeddingPurpose.Document)).Returns((string text, EmbeddingPurpose purpose) => {
            if (committed) transaction!.DbTransaction.Commit();
            else transaction!.DbTransaction.Rollback();
            throw primary;
        });
        NodeService service = new(entities.Object, new EmbeddingCapability(true), producer.Object);
        using MemoryStream data = new("cleanup body"u8.ToArray());

        Exception observed = Assert.CatchAsync(() => service.UploadContent(nodeId, "text/plain", data, 0, true))!;

        Assert.That(observed.InnerException, Is.SameAs(primary));
        entities.Verify(manager => manager.Transaction(), Times.Once);
        producer.Verify(value => value.Embed(It.IsAny<string>(), EmbeddingPurpose.Document), Times.Once);
        Assert.That(connection, Is.Not.Null);
        Assert.That(connection!.State, Is.EqualTo(ConnectionState.Closed));
        Assert.That(database.Scalar<bool>("SELECT content IS NOT NULL FROM node WHERE id = @id", ("id", nodeId)), Is.EqualTo(committed));
    }
}
