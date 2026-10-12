using Backend.Init;
using Npgsql;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Info;

namespace Backend.tests.Fixtures;

/// <summary>
/// throwaway pgvector database on the server named by POSTGRES_CONNECTION whose embedding function is a stub seeded from the md5 of its input text
/// </summary>
public sealed class EmbeddingPostgresDatabase : IDisposable
{
    const string EmbeddingStub = """
        CREATE FUNCTION embedding(model_id text, content text) RETURNS real[] LANGUAGE sql IMMUTABLE AS
        $$ SELECT array_agg(sin(i * (1 + (('x' || substr(md5(content), 1, 7))::bit(28)::int % 100000)))::real) FROM generate_series(1, 3072) i $$
        """;

    readonly string adminConnection;
    readonly string name = "divoid_win_" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// creates the database with the vector extension and the stub embedding function
    /// </summary>
    public EmbeddingPostgresDatabase()
    {
        adminConnection = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")!;
        Execute(adminConnection, $"CREATE DATABASE {name}");
        ConnectionString = new NpgsqlConnectionStringBuilder(adminConnection) { Database = name }.ConnectionString;
        Execute(ConnectionString, "CREATE EXTENSION vector");
        Execute(ConnectionString, EmbeddingStub);
        Entities = new EntityManager(ClientFactory.Create(() => new NpgsqlConnection(ConnectionString), new PostgreInfo(), true));
    }

    /// <summary>
    /// connection string of the created database
    /// </summary>
    public string ConnectionString { get; }

    /// <summary>
    /// entity manager connected to the created database
    /// </summary>
    public IEntityManager Entities { get; }

    static void Execute(string connectionString, string sql)
    {
        using NpgsqlConnection connection = new(connectionString);
        connection.Open();
        using NpgsqlCommand command = new(sql, connection);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// executes a query with named parameters and returns the first column of the first row
    /// </summary>
    public T Scalar<T>(string sql, params (string Name, object Value)[] parameters)
    {
        using NpgsqlConnection connection = new(ConnectionString);
        connection.Open();
        using NpgsqlCommand command = new(sql, connection);
        foreach ((string parameterName, object value) in parameters)
            command.Parameters.AddWithValue(parameterName, value);
        return (T)command.ExecuteScalar()!;
    }

    /// <summary>
    /// creates the schema of the created database
    /// </summary>
    public Task Start() => new DatabaseModelService(Entities).StartAsync(CancellationToken.None);

    /// <inheritdoc />
    public void Dispose()
    {
        NpgsqlConnection.ClearAllPools();
        Execute(adminConnection, $"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
    }
}
