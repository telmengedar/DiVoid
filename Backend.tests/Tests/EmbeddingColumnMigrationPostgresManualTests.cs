using System.Text;
using Backend.Init;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Npgsql;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Info;

namespace Backend.tests.Tests;

/// <summary>
/// manual-lane Postgres tests of the embedding column migration and of the vector(768) read and write paths.
/// every test creates and drops its own database inside the instance named by POSTGRES_CONNECTION, which needs the pgvector extension
/// and a superuser. google_ml_integration is not required: an embedding() function with the same signature is created in the test database.
/// </summary>
[TestFixture, Parallelizable]
public class EmbeddingColumnMigrationPostgresManualTests
{
    const string EmbeddingStub = """
        CREATE FUNCTION embedding(model_id text, content text) RETURNS real[] LANGUAGE sql IMMUTABLE AS
        $$ SELECT array_agg(sin(i * (1 + length(content)))::real) FROM generate_series(1, 3072) i $$
        """;

    const string LiveType = "SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'node'::regclass AND attname = 'embedding'";

    sealed class TestDatabase : IDisposable
    {
        readonly string adminConnection;
        readonly string name = "divoid_mig_" + Guid.NewGuid().ToString("N")[..12];

        public TestDatabase()
        {
            adminConnection = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")!;
            Execute(adminConnection, $"CREATE DATABASE {name}");
            ConnectionString = new NpgsqlConnectionStringBuilder(adminConnection) { Database = name }.ConnectionString;
            Execute(ConnectionString, "CREATE EXTENSION vector");
            Execute(ConnectionString, EmbeddingStub);
            Entities = new EntityManager(ClientFactory.Create(() => new NpgsqlConnection(ConnectionString), new PostgreInfo(), true));
        }

        public string ConnectionString { get; }

        public IEntityManager Entities { get; }

        static void Execute(string connectionString, string sql)
        {
            using NpgsqlConnection connection = new(connectionString);
            connection.Open();
            using NpgsqlCommand command = new(sql, connection);
            command.ExecuteNonQuery();
        }

        public void Execute(string sql) => Execute(ConnectionString, sql);

        public T Scalar<T>(string sql)
        {
            using NpgsqlConnection connection = new(ConnectionString);
            connection.Open();
            using NpgsqlCommand command = new(sql, connection);
            return (T)command.ExecuteScalar()!;
        }

        public string[] Texts(string sql)
        {
            using NpgsqlConnection connection = new(ConnectionString);
            connection.Open();
            using NpgsqlCommand command = new(sql, connection);
            using NpgsqlDataReader reader = command.ExecuteReader();
            List<string> rows = [];
            while (reader.Read())
                rows.Add(reader.GetString(0));
            return rows.ToArray();
        }

        public Task Start() => new DatabaseModelService(Entities).StartAsync(CancellationToken.None);

        public void Dispose()
        {
            NpgsqlConnection.ClearAllPools();
            Execute(adminConnection, $"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
        }
    }

    static async Task SeedLegacyNodes(TestDatabase database, int embedded)
    {
        await database.Start();
        database.Execute("ALTER TABLE node ALTER COLUMN embedding TYPE real[] USING NULL");
        for (int i = 1; i <= embedded; i++)
            database.Execute($"INSERT INTO node (typeid, name, embedding) VALUES (1, 'node {i}', embedding('m', 'node {i}'))");
        database.Execute("INSERT INTO node (typeid, name) VALUES (1, 'without embedding')");
    }

    static async Task<string> Render(AsyncPageResponseWriter<NodeDetails> writer)
    {
        using MemoryStream stream = new();
        await writer.Write(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Test, Parallelizable]
    [Explicit("Manual Postgres test - requires POSTGRES_CONNECTION to a superuser connection with the pgvector extension available.")]
    [Category("PostgresManual")]
    [Description("DiVoid #16141: startup converts real[] to vector(768) keeping the first 768 components, backs up the originals, and a second startup changes nothing")]
    public async Task RealArray_To_Vector768_PreservesValuesAndCreatesBackup()
    {
        using TestDatabase database = new();
        await SeedLegacyNodes(database, 3);

        await database.Start();

        string[] kept = database.Texts("SELECT embedding::text FROM node WHERE embedding IS NOT NULL ORDER BY id");
        string[] expected = database.Texts("SELECT subvector(embedding::vector, 1, 768)::text FROM node_embedding_backup ORDER BY id");
        Assert.Multiple(() => {
            Assert.That(database.Scalar<string>(LiveType), Is.EqualTo("vector(768)"));
            Assert.That(kept, Is.EqualTo(expected).And.Length.EqualTo(3));
            Assert.That(database.Scalar<long>("SELECT count(*) FROM node_embedding_backup"), Is.EqualTo(3));
            Assert.That(database.Scalar<int>("SELECT array_length(embedding, 1) FROM node_embedding_backup LIMIT 1"), Is.EqualTo(3072));
            Assert.That(database.Scalar<long>("SELECT count(*) FROM node WHERE embedding IS NULL"), Is.EqualTo(1));
        });

        database.Execute("UPDATE node SET embedding = subvector(embedding('m', 'changed after migration')::vector, 1, 768) WHERE name = 'node 1'");
        string before = database.Scalar<string>("SELECT embedding::text FROM node WHERE name = 'node 1'");

        await database.Start();

        Assert.Multiple(() => {
            Assert.That(database.Scalar<string>("SELECT embedding::text FROM node WHERE name = 'node 1'"), Is.EqualTo(before));
            Assert.That(database.Scalar<long>("SELECT count(*) FROM node_embedding_backup"), Is.EqualTo(3));
        });
    }

    [Test, Parallelizable]
    [Explicit("Manual Postgres test - requires POSTGRES_CONNECTION to a superuser connection with the pgvector extension available.")]
    [Category("PostgresManual")]
    [Description("DiVoid #16141: a column of an unexpected vector size stops startup and is left as it was")]
    public async Task UnexpectedColumnType_FailsStartupAndLeavesColumn()
    {
        using TestDatabase database = new();
        await database.Start();
        database.Execute("ALTER TABLE node ALTER COLUMN embedding TYPE vector(1536) USING NULL");

        InvalidOperationException? thrown = Assert.ThrowsAsync<InvalidOperationException>(database.Start);

        Assert.Multiple(() => {
            Assert.That(thrown!.Message, Does.Contain("vector(1536)").And.Contain("vector(768)"));
            Assert.That(database.Scalar<string>(LiveType), Is.EqualTo("vector(1536)"));
        });
    }

    [Test, Parallelizable]
    [Explicit("Manual Postgres test - requires POSTGRES_CONNECTION to a superuser connection with the pgvector extension available.")]
    [Category("PostgresManual")]
    [Description("DiVoid #16141: a fresh database gets vector(768), and create, patch, upload and single and multi query search work against it")]
    public async Task FreshDatabase_WritesAndSearchesWith768Dimensions()
    {
        using TestDatabase database = new();
        await database.Start();
        NodeService service = new(database.Entities, new EmbeddingCapability(true));

        NodeDetails created = await service.CreateNode(new NodeDetails { Type = "documentation", Name = "alpha topic" }, callerId: 0);
        NodeDetails other = await service.CreateNode(new NodeDetails { Type = "documentation", Name = "beta topic" }, callerId: 0);
        await service.UploadContent(created.Id, "text/markdown", new MemoryStream("alpha body"u8.ToArray()), callerId: 0, isAdmin: true);
        await service.Patch(other.Id, [new PatchOperation { Op = "replace", Path = "/name", Value = "gamma topic" }], callerId: 0, isAdmin: true, CancellationToken.None);

        string single = await Render(await service.ListPaged(new NodeFilter { Query = "alpha topic", Count = 10 }, callerId: 0, isAdmin: true));
        string multi = await Render(await service.ListPaged(new NodeFilter { Queries = ["alpha topic", "gamma topic"], Count = 10 }, callerId: 0, isAdmin: true));

        Assert.Multiple(() => {
            Assert.That(database.Scalar<string>(LiveType), Is.EqualTo("vector(768)"));
            Assert.That(database.Scalar<long>("SELECT count(*) FROM node WHERE vector_dims(embedding) = 768"), Is.EqualTo(2));
            Assert.That(single, Does.Contain("alpha topic"));
            Assert.That(multi, Does.Contain("gamma topic"));
        });
    }
}
