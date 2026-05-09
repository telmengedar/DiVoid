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
/// Uses a named shared-cache in-memory connection (<c>file::memory:?cache=shared</c> plus a
/// per-fixture unique name) so that all connections from Pooshit.Ocelot's pool see the same
/// in-memory database. A keepalive connection is held open for the fixture lifetime to prevent
/// SQLite from discarding the in-memory database when the pool returns all connections.
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
