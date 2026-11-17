using Backend.Models.Auth;
using Backend.Models.Messages;
using Backend.Models.Users;
using Microsoft.Data.Sqlite;
using Npgsql;
using Pooshit.Json;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Info;
using Pooshit.Ocelot.Schemas;

namespace Backend.tests.Fixtures;

/// <summary>An isolated message-only SQLite or PostgreSQL schema for Teams transport checks.</summary>
public sealed class TeamsRoutingDatabase : IDisposable {
    readonly string connectionString;
    readonly string? schema;
    readonly string? sqlitePath;

    TeamsRoutingDatabase(string connectionString, string? schema, string? sqlitePath, IEntityManager entities) {
        this.connectionString = connectionString;
        this.schema = schema;
        this.sqlitePath = sqlitePath;
        Entities = entities;
    }

    /// <summary>The isolated application entity manager.</summary>
    public IEntityManager Entities { get; }

    /// <summary>The enabled original recipient.</summary>
    public long RecipientId { get; private set; }

    /// <summary>The disabled credentialless external author.</summary>
    public long BridgeId { get; private set; }

    /// <summary>An enabled unrelated administrator.</summary>
    public long OtherId { get; private set; }

    /// <summary>Creates a fresh schema and actor identities.</summary>
    /// <param name="postgres">Whether to use the explicitly supplied disposable PostgreSQL server.</param>
    /// <returns>The initialized isolated database.</returns>
    public static async Task<TeamsRoutingDatabase> CreateAsync(bool postgres) {
        string connectionString;
        string? schema = null;
        string? sqlitePath = null;
        IEntityManager entities;
        if (postgres) {
            string? supplied = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION");
            if (string.IsNullOrEmpty(supplied))
                Assert.Ignore("POSTGRES_CONNECTION is required for the isolated Teams PostgreSQL checks.");
            schema = "teams_" + Guid.NewGuid().ToString("N");
            using NpgsqlConnection connection = new(supplied);
            await connection.OpenAsync();
            using NpgsqlCommand command = new($"CREATE SCHEMA {schema}", connection);
            await command.ExecuteNonQueryAsync();
            connectionString = new NpgsqlConnectionStringBuilder(supplied) { SearchPath = schema, ApplicationName = schema }.ConnectionString;
            entities = new EntityManager(ClientFactory.Create(() => new NpgsqlConnection(connectionString), new PostgreInfo(), true));
        } else {
            string directory = Path.GetFullPath("../../../../.claude/tmp/teams-routing", AppContext.BaseDirectory);
            Directory.CreateDirectory(directory);
            sqlitePath = Path.Combine(directory, "messages-" + Guid.NewGuid().ToString("N") + ".db3");
            connectionString = new SqliteConnectionStringBuilder { DataSource = sqlitePath }.ConnectionString;
            entities = new EntityManager(ClientFactory.Create(() => new SqliteConnection(connectionString), new SQLiteInfo(), false, true));
        }
        TeamsRoutingDatabase database = new(connectionString, schema, sqlitePath, entities);
        try {
            SchemaService schemas = new(entities.DBClient);
            await schemas.CreateOrUpdateSchema<User>();
            await schemas.CreateOrUpdateSchema<ApiKey>();
            await schemas.CreateOrUpdateSchema<Message>();
            await schemas.CreateOrUpdateSchema<TeamsMessageOrigin>();
            database.RecipientId = await database.AddUserAsync(true, ["read", "write"]);
            database.BridgeId = await database.AddUserAsync(false, []);
            database.OtherId = await database.AddUserAsync(true, ["admin"]);
            return database;
        } catch {
            database.Dispose();
            throw;
        }
    }

    /// <summary>Creates an independent connection to this disposable PostgreSQL schema.</summary>
    /// <returns>The unopened fixture-owned connection.</returns>
    public NpgsqlConnection CreatePostgresConnection() => schema == null
        ? throw new InvalidOperationException("This fixture uses SQLite.") : new NpgsqlConnection(connectionString);

    /// <summary>Adds an explicitly configured actor to this isolated schema.</summary>
    /// <param name="enabled">Whether the actor is enabled.</param>
    /// <param name="permissions">The actor permissions.</param>
    /// <returns>The created actor identifier.</returns>
    public Task<long> AddUserAsync(bool enabled, string[] permissions) => Entities.Insert<User>()
        .Columns(user => user.Name, user => user.Enabled, user => user.Permissions, user => user.CreatedAt)
        .Values("routing-test-actor", enabled, Json.WriteString(permissions), DateTime.UtcNow)
        .ReturnID().ExecuteAsync();

    /// <inheritdoc />
    public void Dispose() {
        if (schema != null) {
            using NpgsqlConnection pool = new(connectionString);
            NpgsqlConnection.ClearPool(pool);
            using NpgsqlConnection connection = new(connectionString);
            connection.Open();
            using NpgsqlCommand command = new($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
            command.ExecuteNonQuery();
            NpgsqlConnection.ClearPool(connection);
        } else {
            using SqliteConnection pool = new(connectionString);
            SqliteConnection.ClearPool(pool);
            File.Delete(sqlitePath!);
        }
    }
}
