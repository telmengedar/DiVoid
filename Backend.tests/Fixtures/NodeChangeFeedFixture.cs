using System.Net.Http.Headers;
using System.Reflection;
using Backend.Models.Auth;
using Backend.Models.Nodes;
using Backend.Models.Users;
using Backend.Services.Auth;
using Backend.Services.NodeChanges;
using Backend.Services.Nodes;
using Backend.Services.Users;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Info;

namespace Backend.tests.Fixtures;

/// <summary>An isolated real HTTP graph feed with independent SQLite or PostgreSQL connections.</summary>
public sealed class NodeChangeFeedFixture : IDisposable {
    readonly EmbeddingPostgresDatabase? postgres;
    readonly string? sqlitePath;

    NodeChangeFeedFixture(EmbeddingPostgresDatabase? postgres, string? sqlitePath, IEntityManager database, NodeChangeReadProbe probe, bool authEnabled, Action<IServiceCollection>? configureServices) {
        this.postgres = postgres;
        this.sqlitePath = sqlitePath;
        Database = database;
        Probe = probe;
        Host = new JwtAuthFixture(database, configureServices, authEnabled);
    }

    /// <summary>The actual application host.</summary>
    public JwtAuthFixture Host { get; }

    /// <summary>The underlying database with independent connections.</summary>
    public IEntityManager Database { get; }

    /// <summary>The real provider read counter and registration barrier.</summary>
    public NodeChangeReadProbe Probe { get; }

    /// <summary>The authenticated original private owner.</summary>
    public long OwnerId { get; private set; }

    /// <summary>The selected private direct-root anchor.</summary>
    public long RootId { get; private set; }

    /// <summary>The synthetic read/write credential for the owner.</summary>
    public ApiKeyDetails Key { get; private set; } = null!;

    /// <summary>The shared process-local publication boundary.</summary>
    public NodeChangeFeed Feed => Host.Services.GetRequiredService<NodeChangeFeed>();

    /// <summary>The actual node producer.</summary>
    public INodeService Nodes => Host.Services.GetRequiredService<INodeService>();

    /// <summary>The actual key authority producer.</summary>
    public IApiKeyService Keys => Host.Services.GetRequiredService<IApiKeyService>();

    /// <summary>The actual user authority producer.</summary>
    public IUserService Users => Host.Services.GetRequiredService<IUserService>();

    /// <summary>Creates a file-backed rollback-journal or disposable PostgreSQL fixture.</summary>
    /// <param name="postgres">Whether PostgreSQL is required.</param>
    /// <param name="sharedCache">Whether SQLite uses its shared-cache provider mode.</param>
    /// <param name="authEnabled">Whether normal authentication is enabled.</param>
    /// <param name="configureServices">The optional real HTTP write instrumentation.</param>
    /// <returns>The initialized isolated fixture.</returns>
    public static async Task<NodeChangeFeedFixture> CreateAsync(bool postgres = false, bool sharedCache = false, bool authEnabled = true, Action<IServiceCollection>? configureServices = null) {
        EmbeddingPostgresDatabase? pg = null;
        string? path = null;
        IEntityManager database;
        if (postgres) {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")))
                Assert.Ignore("POSTGRES_CONNECTION is required for the private graph-feed PostgreSQL fixture.");
            pg = new EmbeddingPostgresDatabase();
            await pg.Start();
            database = pg.Entities;
        } else {
            path = Path.Combine(Path.GetTempPath(), "divoid_feed_" + Guid.NewGuid().ToString("N") + ".db3");
            string connection = new SqliteConnectionStringBuilder {
                DataSource = path, Cache = sharedCache ? SqliteCacheMode.Shared : SqliteCacheMode.Private, DefaultTimeout = 2
            }.ConnectionString;
            database = new EntityManager(ClientFactory.Create(() => new SqliteConnection(connection), new SQLiteInfo(), false, true));
            await database.DBClient.NonQueryAsync("PRAGMA journal_mode=DELETE");
        }
        (IDBClient client, NodeChangeReadProbe probe) = NodeChangeReadProbe.Wrap(database.DBClient);
        database = new EntityManager(client);
        NodeChangeFeedFixture fixture = new(pg, path, database, probe, authEnabled, configureServices);
        fixture.OwnerId = await fixture.AddUserAsync();
        fixture.RootId = (await fixture.Nodes.CreateNode(new NodeDetails { Name = "private-root-canary", Access = NodeAccess.None }, fixture.OwnerId)).Id;
        fixture.Key = await fixture.AddKeyAsync(fixture.OwnerId, ["read", "write"]);
        return fixture;
    }

    /// <summary>Adds an enabled fixture identity.</summary>
    /// <param name="permissions">The optional DB-user permissions.</param>
    /// <returns>The new user identifier.</returns>
    public Task<long> AddUserAsync(string permissions = "[]") => Database.Insert<User>()
        .Columns(user => user.Name, user => user.Enabled, user => user.Permissions, user => user.CreatedAt)
        .Values("fixture-user", true, permissions, DateTime.UtcNow).ReturnID().ExecuteAsync();

    /// <summary>Adds one synthetic API key.</summary>
    /// <param name="owner">The owning user.</param>
    /// <param name="permissions">The effective key permissions.</param>
    /// <returns>The new fixture credential.</returns>
    public Task<ApiKeyDetails> AddKeyAsync(long owner, string[] permissions) => Keys.CreateApiKey(new ApiKeyParameters { UserId = owner, Permissions = permissions });

    /// <summary>Creates a client authenticated with the supplied synthetic key.</summary>
    /// <param name="key">The optional fixture key.</param>
    /// <returns>The fixture-owned HTTP client.</returns>
    public HttpClient Client(ApiKeyDetails? key = null) {
        HttpClient client = Host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (key ?? Key).PlaintextKey);
        return client;
    }

    /// <summary>Creates a direct private member.</summary>
    /// <param name="type">The exact member type.</param>
    /// <returns>The new node identifier.</returns>
    public async Task<long> MemberAsync(string type = "configuration") => (await Nodes.CreateNode(new NodeDetails {
        Name = "private-value-canary", Type = type, RootNodeId = RootId, Access = NodeAccess.None
    }, OwnerId)).Id;

    internal T PostgresScalar<T>(string sql) => postgres!.Scalar<T>(sql);

    /// <inheritdoc />
    public void Dispose() {
        Host.Dispose();
        postgres?.Dispose();
        if (sqlitePath != null) {
            SqliteConnection.ClearAllPools();
            File.Delete(sqlitePath);
        }
    }
}

/// <summary>Forwards SQL to a real provider while counting and optionally pausing reads.</summary>
public class NodeChangeReadProbe : DispatchProxy {
    IDBClient inner = null!;
    /// <summary>The optional real-read barrier.</summary>
    public Func<string, Task>? BeforeRead { get; set; }

    /// <summary>The optional transaction preparation observation.</summary>
    public Action? BeforeTransaction { get; set; }
    int reads;

    /// <summary>The observed SQL read count.</summary>
    public int Reads => Volatile.Read(ref reads);

    /// <summary>Wraps the actual provider without changing its dialect or transaction semantics.</summary>
    /// <param name="client">The actual provider.</param>
    /// <param name="beforeRead">The optional read barrier.</param>
    /// <returns>The proxy and selected probe.</returns>
    public static (IDBClient Client, NodeChangeReadProbe Probe) Wrap(IDBClient client, Func<string, Task>? beforeRead = null) {
        IDBClient proxy = Create<IDBClient, NodeChangeReadProbe>();
        NodeChangeReadProbe probe = (NodeChangeReadProbe)(object)proxy;
        probe.inner = client;
        probe.BeforeRead = beforeRead;
        return (proxy, probe);
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
        if (targetMethod!.Name == "Transaction")
            BeforeTransaction?.Invoke();
        string? sql = args?.OfType<string>().FirstOrDefault();
        if (sql?.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) == true) {
            Interlocked.Increment(ref reads);
            if (BeforeRead != null)
                BeforeRead(sql).GetAwaiter().GetResult();
        }
        try {
            return targetMethod!.Invoke(inner, args);
        } catch (TargetInvocationException exception) when (exception.InnerException != null) {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}
