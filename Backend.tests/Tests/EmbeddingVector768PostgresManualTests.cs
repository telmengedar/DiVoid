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
/// manual-lane Postgres test of the vector(768) create, write and search paths against a real pgvector column
/// </summary>
[TestFixture, Parallelizable]
public class EmbeddingVector768PostgresManualTests
{
    const string EmbeddingStub = """
        CREATE FUNCTION embedding(model_id text, content text) RETURNS real[] LANGUAGE sql IMMUTABLE AS
        $$ SELECT array_agg(sin(i * (1 + length(content)))::real) FROM generate_series(1, 3072) i $$
        """;

    const string LiveType = "SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'node'::regclass AND attname = 'embedding'";

    sealed class TestDatabase : IDisposable
    {
        readonly string adminConnection;
        readonly string name = "divoid_v768_" + Guid.NewGuid().ToString("N")[..12];

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

        public T Scalar<T>(string sql)
        {
            using NpgsqlConnection connection = new(ConnectionString);
            connection.Open();
            using NpgsqlCommand command = new(sql, connection);
            return (T)command.ExecuteScalar()!;
        }

        public Task Start() => new DatabaseModelService(Entities).StartAsync(CancellationToken.None);

        public void Dispose()
        {
            NpgsqlConnection.ClearAllPools();
            Execute(adminConnection, $"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
        }
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
    [Description("a fresh database gets vector(768), and create, patch, upload and single and multi query search work against it")]
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
