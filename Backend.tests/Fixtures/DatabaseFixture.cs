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
/// Spins up an isolated, file-backed SQLite <see cref="IEntityManager"/> with the full
/// DiVoid schema applied. Each test that needs a clean database should create a new
/// instance (or inherit from <see cref="DatabaseTest"/>).
///
/// The database file lives in a temp directory that is cleaned up on <see cref="Dispose"/>.
/// Using a real file (rather than :memory:) avoids connection-sharing complexity with
/// Pooshit.Ocelot's own connection pooling.
/// </summary>
public sealed class DatabaseFixture : IDisposable
{
    readonly string _tempDir;
    readonly string _dbPath;

    public IEntityManager EntityManager { get; }

    public DatabaseFixture()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "divoid_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db3");

        // Mirror exactly what DatabaseExtensions.ConfigureDatabaseService does for SQLite.
        EntityManager = new EntityManager(
            ClientFactory.Create(
                () => new SqliteConnection($"Data Source={_dbPath}"),
                new SQLiteInfo(),
                false,
                true));

        // Apply the full schema synchronously — tests need tables to exist before running.
        ApplySchema().GetAwaiter().GetResult();
    }

    async Task ApplySchema()
    {
        DatabaseModelService svc = new(EntityManager);
        await svc.StartAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        // EntityManager itself has no IDisposable, but we want to clean up the temp file.
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }
}
