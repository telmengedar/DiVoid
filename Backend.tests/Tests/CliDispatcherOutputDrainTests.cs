using Backend.Cli;
using Backend.tests.Fixtures;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// runs the CLI verbs in-process against a slow console and checks every logged line is written before the call returns
/// </summary>
[TestFixture, NonParallelizable]
[Description("Console.Out and environment variables are process-wide, so this fixture must not overlap other tests.")]
public class CliDispatcherOutputDrainTests
{
    static readonly TimeSpan WriteDelay = TimeSpan.FromMilliseconds(50);
    const string Pepper = "drain-test-pepper-value-that-is-at-least-32-bytes-00";

    static async Task<(int ExitCode, string Output)> RunWithSlowConsole(params string[] arguments)
    {
        string database = Path.Combine(Path.GetTempPath(), $"drain-cli-{Guid.NewGuid():N}.db3");
        string[] names = ["Database__Type", "Database__Source", "DIVOID_KEY_PEPPER"];
        string?[] originals = names.Select(Environment.GetEnvironmentVariable).ToArray();
        TextWriter originalOut = Console.Out;
        SlowRecordingWriter writer = new(WriteDelay);

        Environment.SetEnvironmentVariable("Database__Type", "Sqlite");
        Environment.SetEnvironmentVariable("Database__Source", database);
        Environment.SetEnvironmentVariable("DIVOID_KEY_PEPPER", Pepper);
        Console.SetOut(writer);
        try {
            int exitCode = await CliDispatcher.RunAsync(arguments);
            return (exitCode, writer.Text);
        } finally {
            Console.SetOut(originalOut);
            for (int i = 0; i < names.Length; i++)
                Environment.SetEnvironmentVariable(names[i], originals[i]);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Test]
    public async Task BackfillEmbeddings_SlowConsole_AllLinesWrittenBeforeReturn()
    {
        (int exitCode, string output) = await RunWithSlowConsole("backfill-embeddings", "--ids", "5, 7,9", "--long-only");

        Assert.That(output, Does.Contain("event=backfill.options ids=3 long_only=True"));
        Assert.That(output, Does.Contain("event=backfill.skip"));
        Assert.That(output, Does.Contain("event=cli.backfill-embeddings exitCode=0"));
        Assert.That(exitCode, Is.Zero);
    }

    [Test]
    public async Task LayoutNodes_SlowConsole_AllLinesWrittenBeforeReturn()
    {
        (int exitCode, string output) = await RunWithSlowConsole("layout-nodes");

        Assert.That(output, Does.Contain("event=layout.complete"));
        Assert.That(output, Does.Contain("event=cli.layout-nodes exitCode=0"));
        Assert.That(exitCode, Is.Zero);
    }

    [Test]
    public async Task CreateAdmin_SlowConsole_LogLineWrittenBeforeReturn()
    {
        (int exitCode, string output) = await RunWithSlowConsole("create-admin", "--name", "drain-admin");

        Assert.That(output, Does.Contain("event=cli.create-admin"));
        Assert.That(output, Does.Contain("exitCode=0"));
        Assert.That(output, Does.Contain("Key (store now"));
        Assert.That(exitCode, Is.Zero);
    }

    [Test]
    public async Task UnknownVerb_ReturnsOne()
    {
        int exitCode = await CliDispatcher.RunAsync(["no-such-verb"]);

        Assert.That(exitCode, Is.EqualTo(1));
    }
}
