using Backend.Init;
using Backend.Models.Auth;
using Backend.Models.Nodes;
using Microsoft.Data.Sqlite;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Info;
using Pooshit.Ocelot.Schemas;

namespace Backend.tests.Fixtures;

/// <summary>
/// Spins up an isolated, shared in-memory SQLite <see cref="IEntityManager"/> with the full
/// DiVoid schema applied. Each test that needs a clean database should create a new
/// instance (or inherit from <see cref="DatabaseTest"/>).
///
/// Why shared-cache + keepalive rather than plain <c>:memory:</c>:
/// <see cref="ClientFactory.Create(System.Func{System.Data.Common.DbConnection},Pooshit.Ocelot.Info.IDBInfo,bool,bool)"/>
/// accepts a connection factory (not a single connection) and creates a new
/// <see cref="System.Data.Common.DbConnection"/> for each operation. SQLite's plain
/// <c>:memory:</c> databases are per-connection — every new connection sees a fresh,
/// empty database — so the schema written by the first connection would be invisible
/// to the next. The named shared-cache URI (<c>file:&lt;name&gt;?mode=memory&amp;cache=shared</c>)
/// makes all connections within the process share one in-memory database identified
/// by name. The keepalive connection prevents SQLite from destroying that in-memory
/// database the moment the pool temporarily holds no open connections.
/// </summary>
public sealed class DatabaseFixture : IDisposable
{
    readonly string dbName;
    readonly SqliteConnection keepalive;

    /// <summary>
    /// entity manager for this fixture
    /// </summary>
    public IEntityManager EntityManager { get; }

    /// <summary>
    /// creates a new database fixture
    /// </summary>
    public DatabaseFixture()
    {
        dbName = "divoid_test_" + Guid.NewGuid().ToString("N");
        string connStr = $"Data Source=file:{dbName}?mode=memory&cache=shared;";

        // Keepalive connection prevents SQLite from destroying the in-memory DB when
        // Ocelot's pool temporarily holds no connections.
        keepalive = new SqliteConnection(connStr);
        keepalive.Open();

        EntityManager = new EntityManager(
            ClientFactory.Create(
                () => new SqliteConnection(connStr),
                new SQLiteInfo(),
                false,
                true));

        ApplySchema().GetAwaiter().GetResult();
    }

    async Task ApplySchema()
    {
        DatabaseModelService svc = new(EntityManager);
        await svc.StartAsync(CancellationToken.None);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        keepalive.Dispose();
    }
}
