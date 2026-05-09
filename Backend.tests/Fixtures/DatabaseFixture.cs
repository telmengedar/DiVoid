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
/// Spins up an isolated in-memory SQLite <see cref="IEntityManager"/> with the full
/// DiVoid schema applied. Each test that needs a clean database should create a new
/// instance (or inherit from <see cref="DatabaseTest"/>).
///
/// A single <see cref="SqliteConnection"/> is opened for the lifetime of the fixture
/// and passed directly to <see cref="ClientFactory.Create(System.Data.Common.DbConnection,Pooshit.Ocelot.Info.IDBInfo)"/>.
/// Ocelot reuses that one connection for every operation, so the plain
/// <c>:memory:</c> database persists for the entire fixture lifetime without needing
/// a shared-cache URI, a keepalive connection, or a per-fixture GUID name.
/// </summary>
public sealed class DatabaseFixture : IDisposable
{
    readonly SqliteConnection connection;

    /// <summary>
    /// entity manager for this fixture
    /// </summary>
    public IEntityManager EntityManager { get; }

    /// <summary>
    /// creates a new database fixture
    /// </summary>
    public DatabaseFixture()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        EntityManager = new EntityManager(
            ClientFactory.Create(connection, new SQLiteInfo()));

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
        connection.Dispose();
    }
}
