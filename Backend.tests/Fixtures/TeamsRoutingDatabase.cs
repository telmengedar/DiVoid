using Backend.Models.Auth;
using Backend.Models.Messages;
using Backend.Models.Nodes;
using System.Security.Cryptography;
using System.Text;
using Backend.Models.Users;
using Microsoft.Data.Sqlite;
using Npgsql;
using Pooshit.Json;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Info;
using Pooshit.Ocelot.Schemas;

namespace Backend.tests.Fixtures;

/// <summary>An isolated graph-and-message SQLite or PostgreSQL schema for Teams transport checks.</summary>
public sealed class TeamsRoutingDatabase : IDisposable {
    static readonly SemaphoreSlim postgresFixtures = new(1, 1);
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

    /// <summary>The initial protected graph binding.</summary>
    public long BindingId { get; private set; }

    /// <summary>The enabled original recipient.</summary>
    public long RecipientId { get; private set; }

    /// <summary>A disabled actor for negative authorization checks.</summary>
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
            await postgresFixtures.WaitAsync();
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
            await schemas.CreateOrUpdateSchema<NodeType>();
            await schemas.CreateOrUpdateSchema<NodeHook>();
            if (postgres)
                entities.DBClient.NonQuery("CREATE TABLE node (id BIGSERIAL PRIMARY KEY, typeid BIGINT NOT NULL, name TEXT, contenttype TEXT, content BYTEA, substance TEXT, skipembedding BOOLEAN NOT NULL DEFAULT TRUE, status TEXT, severity INTEGER, refinement TEXT, x DOUBLE PRECISION NOT NULL DEFAULT 0, y DOUBLE PRECISION NOT NULL DEFAULT 0, ownerid BIGINT NOT NULL DEFAULT 0, access INTEGER NOT NULL DEFAULT 3, rootnodeid BIGINT, created TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, lastupdate TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP)");
            else
                await schemas.CreateOrUpdateSchema<Node>();
            database.RecipientId = await database.AddUserAsync(true, ["read", "write"]);
            database.BridgeId = await database.AddUserAsync(false, []);
            database.OtherId = await database.AddUserAsync(true, ["admin"]);
            database.BindingId = await database.AddBindingAsync(database.RecipientId, database.RecipientId, "divoid");
            return database;
        } catch {
            database.Dispose();
            throw;
        }
    }

    /// <summary>Creates a protected graph record with a node-bound synthetic credential proof.</summary>
    /// <param name="ownerId">The actual graph record owner.</param>
    /// <param name="recipientId">The selected enabled recipient.</param>
    /// <param name="tag">The receiving session tag.</param>
    /// <param name="secret">The synthetic active bot secret.</param>
    /// <returns>The new graph node identifier.</returns>
    public async Task<long> AddBindingAsync(long ownerId, long recipientId, string tag, string secret = "synthetic-fixture-secret") {
        long? typeId = await Entities.Load<NodeType>(type => type.Id).Where(type => type.Type == "teams-bot-binding").ExecuteScalarAsync<long?>();
        typeId ??= await Entities.Insert<NodeType>().Columns(type => type.Type).Values("teams-bot-binding").ReturnID().ExecuteAsync();
        long id = await Entities.Insert<Node>().Columns(node => node.TypeId, node => node.Name, node => node.OwnerId,
            node => node.Access, node => node.SkipEmbedding, node => node.Created, node => node.LastUpdate,
            node => node.X, node => node.Y)
            .Values(typeId.Value, TeamsWebhookFixture.ClientId, ownerId, NodeAccess.Read, true, DateTime.UtcNow, DateTime.UtcNow, 0d, 0d).ReturnID().ExecuteAsync();
        await SetBindingAsync(id, ownerId, recipientId, tag, secret);
        return id;
    }

    /// <summary>Updates the concrete fixture graph JSON using the independent bootstrap recipe.</summary>
    /// <param name="nodeId">The actual record identifier.</param>
    /// <param name="ownerId">The actual owner bound by the proof.</param>
    /// <param name="recipientId">The receiving identity.</param>
    /// <param name="tag">The receiving tag.</param>
    /// <param name="secret">The synthetic active secret.</param>
    /// <returns>The asynchronous graph write.</returns>
    public Task SetBindingAsync(long nodeId, long ownerId, long recipientId, string tag, string secret = "synthetic-fixture-secret") {
        string canonical = string.Join((char)10, new[] { "DiVoid/TeamsGraphBinding/v1", TeamsWebhookFixture.ClientId,
            nodeId.ToString(System.Globalization.CultureInfo.InvariantCulture), ownerId.ToString(System.Globalization.CultureInfo.InvariantCulture), "" });
        string proof = "hmac-sha256-v1:" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        byte[] content = Encoding.UTF8.GetBytes(Json.WriteString(new Dictionary<string, object> { ["botId"] = TeamsWebhookFixture.ClientId, ["userId"] = recipientId, ["tag"] = tag, ["proof"] = proof }));
        return Entities.Update<Node>().Set(node => node.Content == content, node => node.ContentType == "application/json").Where(node => node.Id == nodeId).ExecuteAsync();
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
            postgresFixtures.Release();
        } else {
            using SqliteConnection pool = new(connectionString);
            SqliteConnection.ClearPool(pool);
            File.Delete(sqlitePath!);
        }
    }
}
