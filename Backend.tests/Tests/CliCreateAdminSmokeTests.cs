using Backend.tests.Fixtures;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// runs the built backend executable with the create-admin verb and checks exit code, output and database
/// </summary>
[TestFixture, Parallelizable]
public class CliCreateAdminSmokeTests {

    [Test, Parallelizable]
    public async Task CreateAdmin_MissingPepper_ExitsOneWithErrorAndWritesNoUser() {
        string database = Path.Combine(Path.GetTempPath(), $"create-admin-cli-{Guid.NewGuid():N}.db3");

        try {
            CliResult result = await CliProcess.RunAsync(["create-admin", "--name", "smoke-admin"], environment => {
                environment["Database__Type"] = "Sqlite";
                environment["Database__Source"] = database;
                environment["Auth__Enabled"] = "true";
                environment.Remove("DIVOID_KEY_PEPPER");
            });

            Assert.Multiple(() => {
                Assert.That(result.ExitCode, Is.EqualTo(1), result.StdErr);
                Assert.That(result.StdErr, Does.Contain("error: DIVOID_KEY_PEPPER"));
                Assert.That(result.StdErr, Does.Not.Contain("Unhandled exception"));
                Assert.That(result.StdOut, Does.Contain("event=cli.create-admin exitCode=1 reason=missing_pepper"));
                Assert.That(result.StdOut, Does.Not.Contain("Key (store now"));
                Assert.That(CountUsers(database), Is.Zero);
            });
        } finally {
            File.Delete(database);
        }
    }


    static long CountUsers(string database) {
        using SqliteConnection connection = new($"Data Source={database};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM divoid_user";
        return (long)command.ExecuteScalar()!;
    }
}
