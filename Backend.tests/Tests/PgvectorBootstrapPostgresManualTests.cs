using Backend.tests.Fixtures;

namespace Backend.tests.Tests;

/// <summary>
/// manual-lane Postgres test of the pgvector bootstrap against databases created without the extension
/// </summary>
[TestFixture, Parallelizable]
public class PgvectorBootstrapPostgresManualTests {
    const string LiveType = "SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'node'::regclass AND attname = 'embedding'";
    const string ExtensionCount = "SELECT count(*) FROM pg_extension WHERE extname = 'vector'";

    [Test, Parallelizable]
    [Explicit("Manual Postgres test - requires POSTGRES_CONNECTION to a superuser connection with the pgvector extension available.")]
    [Category("PostgresManual")]
    [Description("A superuser start enables the missing extension before the schema, so node.embedding becomes vector(768).")]
    public async Task StartAsync_ExtensionMissingAndCreatable_EnablesExtensionAndSchemaSucceeds() {
        using PgvectorBareDatabase database = new();

        await PgvectorBareDatabase.Start(database.AdminEntities);

        Assert.Multiple(() => {
            Assert.That(database.Scalar<long>(ExtensionCount), Is.EqualTo(1));
            Assert.That(database.Scalar<string>(LiveType), Is.EqualTo("vector(768)"));
        });
    }


    [Test, Parallelizable]
    [Explicit("Manual Postgres test - requires POSTGRES_CONNECTION to a superuser connection with the pgvector extension available.")]
    [Category("PostgresManual")]
    [Description("A role which may not create the missing extension fails startup naming pgvector and the remedy, and leaves the extension absent.")]
    public void StartAsync_ExtensionMissingAndNotPermitted_FailsNamingPgvector() {
        using PgvectorBareDatabase database = new();
        database.RevokeDatabaseCreate();
        database.GrantSchema();

        InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(() => PgvectorBareDatabase.Start(database.RoleEntities));

        Assert.Multiple(() => {
            Assert.That(exception!.Message, Does.Contain("pgvector"));
            Assert.That(exception.Message, Does.Contain("SQLSTATE 42501"));
            Assert.That(exception.Message, Does.Contain("CREATE EXTENSION vector"));
            Assert.That(database.Scalar<long>(ExtensionCount), Is.EqualTo(0));
        });
    }


    [Test, Parallelizable]
    [Explicit("Manual Postgres test - requires POSTGRES_CONNECTION to a superuser connection with the pgvector extension available.")]
    [Category("PostgresManual")]
    [Description("A role which may not create extensions starts and creates the schema when the extension is already enabled.")]
    public async Task StartAsync_ExtensionPresentAndUnprivileged_Starts() {
        using PgvectorBareDatabase database = new();
        database.RevokeDatabaseCreate();
        database.EnableExtension();
        database.GrantSchema();

        await PgvectorBareDatabase.Start(database.RoleEntities);

        Assert.That(database.Scalar<string>(LiveType), Is.EqualTo("vector(768)"));
    }
}
