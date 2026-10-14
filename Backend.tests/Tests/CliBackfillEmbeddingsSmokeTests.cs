using Backend.tests.Fixtures;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// runs the built backend executable with the backfill-embeddings verb and checks exit code and output
/// </summary>
[TestFixture, Parallelizable]
public class CliBackfillEmbeddingsSmokeTests
{
    static async Task<CliResult> Run(params string[] arguments) {
        string database = Path.Combine(Path.GetTempPath(), $"backfill-cli-{Guid.NewGuid():N}.db3");

        try {
            return await CliProcess.RunAsync(["backfill-embeddings", .. arguments], environment => {
                environment["Database__Type"] = "Sqlite";
                environment["Database__Source"] = database;
            });
        } finally {
            File.Delete(database);
        }
    }


    [Test, Parallelizable]
    public async Task BackfillEmbeddings_IdsAndLongOnly_AreAccepted()
    {
        CliResult result = await Run("--ids", "5, 7,9", "--long-only");

        Assert.That(result.ExitCode, Is.Zero, result.StdErr);
        Assert.That(result.StdOut, Does.Contain("event=backfill.options ids=3 long_only=True"));
        Assert.That(result.StdOut, Does.Contain("event=backfill.skip"));
    }

    [Test, Parallelizable]
    public async Task BackfillEmbeddings_NoOptions_IsAccepted()
    {
        CliResult result = await Run();

        Assert.That(result.ExitCode, Is.Zero, result.StdErr);
        Assert.That(result.StdOut, Does.Contain("event=backfill.options ids=0 long_only=False"));
        Assert.That(result.StdOut, Does.Contain("event=backfill.skip"));
    }

    [Test, Parallelizable]
    public async Task BackfillEmbeddings_IdsWithNonNumericEntry_FailsWithoutRunning()
    {
        CliResult result = await Run("--ids", "5,abc");

        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.StdErr, Does.Contain("--ids"));
        Assert.That(result.StdOut, Does.Not.Contain("event=backfill"));
    }

    [Test, Parallelizable]
    public async Task BackfillEmbeddings_IdsWithoutValue_FailsWithoutRunning()
    {
        CliResult result = await Run("--ids");

        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.StdOut, Does.Not.Contain("event=backfill"));
    }

    [Test, Parallelizable]
    public async Task BackfillEmbeddings_UnknownOption_FailsWithoutRunning()
    {
        CliResult result = await Run("--long-onyl");

        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.StdErr, Does.Contain("--long-onyl"));
        Assert.That(result.StdOut, Does.Not.Contain("event=backfill"));
    }
}
