using Backend.Init;
using Backend.tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Info;

namespace Backend.tests.Tests;

/// <summary>
/// pins the decisions of the pgvector bootstrap against a scripted client
/// </summary>
[TestFixture, Parallelizable]
public class PgvectorBootstrapTests {
    const string Detect = "SELECT extversion FROM pg_extension WHERE extname = 'vector'";
    const string Create = "CREATE EXTENSION IF NOT EXISTS vector";

    static PgvectorBootstrap Bootstrap(IDBClient client) => new(client, NullLogger.Instance);


    [Test, Parallelizable]
    [Description("A role which may not create extensions must start normally when the extension is already there.")]
    public async Task EnsureAsync_ExtensionPresent_IssuesNoCreate() {
        IDBClient client = ScriptedDbClient.Create(new PostgreInfo(), _ => "0.8.0", _ => 0, out ScriptedDbClient script);

        await Bootstrap(client).EnsureAsync();

        Assert.That(script.Statements, Is.EqualTo(new[] { Detect }));
    }


    [Test, Parallelizable]
    [Description("A missing extension is created and its version read back.")]
    public async Task EnsureAsync_ExtensionMissing_IssuesCreate() {
        bool created = false;
        IDBClient client = ScriptedDbClient.Create(new PostgreInfo(), _ => created ? "0.8.0" : null, _ => { created = true; return 0; }, out ScriptedDbClient script);

        await Bootstrap(client).EnsureAsync();

        Assert.That(script.Statements, Is.EqualTo(new[] { Detect, Create, Detect }));
    }


    [Test, Parallelizable]
    [Description("A failed create refuses startup with a message naming pgvector, the database, the cause and the remedy.")]
    public void EnsureAsync_CreateFails_ThrowsNamingPgvectorAndCause() {
        Exception cause = new("Error executing 'CREATE EXTENSION IF NOT EXISTS vector'", new PostgresException("permission denied to create extension \"vector\"", "ERROR", "ERROR", "42501"));
        IDBClient client = ScriptedDbClient.Create(new PostgreInfo(), statement => statement == Detect ? null : "divoid_prod", _ => throw cause, out ScriptedDbClient _);

        InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(() => Bootstrap(client).EnsureAsync());

        Assert.Multiple(() => {
            Assert.That(exception!.Message, Does.Contain("pgvector is required"));
            Assert.That(exception.Message, Does.Contain("divoid_prod"));
            Assert.That(exception.Message, Does.Contain("SQLSTATE 42501: permission denied to create extension"));
            Assert.That(exception.Message, Does.Contain("CREATE EXTENSION vector"));
            Assert.That(exception.InnerException, Is.SameAs(cause));
        });
    }


    [Test, Parallelizable]
    [Description("A failed create whose innermost cause is not a Postgres error still names that cause in the refusal.")]
    public void EnsureAsync_CreateFailsWithoutPostgresCause_NamesCauseText() {
        Exception cause = new("Error executing 'CREATE EXTENSION IF NOT EXISTS vector'", new IOException("connection reset by peer"));
        IDBClient client = ScriptedDbClient.Create(new PostgreInfo(), statement => statement == Detect ? null : "divoid_prod", _ => throw cause, out ScriptedDbClient _);

        InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(() => Bootstrap(client).EnsureAsync());

        Assert.That(exception!.Message, Does.Contain("connection reset by peer"));
    }


    [Test, Parallelizable]
    [Description("An installed version older than the minimum refuses startup naming that version and issues no create.")]
    public void EnsureAsync_InstalledVersionTooOld_Throws() {
        IDBClient client = ScriptedDbClient.Create(new PostgreInfo(), _ => "0.6.2", _ => 0, out ScriptedDbClient script);

        InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(() => Bootstrap(client).EnsureAsync());

        Assert.Multiple(() => {
            Assert.That(exception!.Message, Does.Contain("0.6.2"));
            Assert.That(exception.Message, Does.Contain("pgvector"));
            Assert.That(script.Statements, Is.EqualTo(new[] { Detect }));
        });
    }


    [Test, Parallelizable]
    [Description("A freshly created extension older than the minimum refuses startup naming that version.")]
    public void EnsureAsync_CreatedVersionTooOld_Throws() {
        bool created = false;
        IDBClient client = ScriptedDbClient.Create(new PostgreInfo(), _ => created ? "0.6.0" : null, _ => { created = true; return 0; }, out ScriptedDbClient _);

        InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(() => Bootstrap(client).EnsureAsync());

        Assert.That(exception!.Message, Does.Contain("0.6.0"));
    }


    [Test, Parallelizable]
    [Description("The minimum version itself is accepted.")]
    public void EnsureAsync_MinimumVersion_Starts() {
        IDBClient client = ScriptedDbClient.Create(new PostgreInfo(), _ => "0.7.0", _ => 0, out ScriptedDbClient _);

        Assert.DoesNotThrowAsync(() => Bootstrap(client).EnsureAsync());
    }


    [Test, Parallelizable]
    [Description("A SQLite database has no extensions, so no extension statement is issued.")]
    public async Task EnsureAsync_Sqlite_IssuesNoExtensionSql() {
        IDBClient client = ScriptedDbClient.Create(new SQLiteInfo(), _ => null, _ => 0, out ScriptedDbClient script);

        await Bootstrap(client).EnsureAsync();

        Assert.That(script.Statements, Is.Empty);
    }
}
