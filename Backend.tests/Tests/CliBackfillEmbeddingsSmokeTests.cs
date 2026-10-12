using System.Diagnostics;
using Backend.tests.Fixtures;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// runs the built backend executable with the backfill-embeddings verb and checks exit code and output
/// </summary>
[TestFixture, Parallelizable]
public class CliBackfillEmbeddingsSmokeTests
{
    static async Task<CliResult> Run(params string[] arguments)
    {
        string database = Path.Combine(Path.GetTempPath(), $"backfill-cli-{Guid.NewGuid():N}.db3");
        ProcessStartInfo start = new("dotnet") {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Backend.dll"));
        start.ArgumentList.Add("backfill-embeddings");
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["Database__Type"] = "Sqlite";
        start.Environment["Database__Source"] = database;

        try {
            using Process process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new CliResult(process.ExitCode, await stdout, await stderr);
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
