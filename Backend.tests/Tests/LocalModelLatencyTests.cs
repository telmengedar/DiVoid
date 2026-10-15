using System.Diagnostics;
using System.Text;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// LocalModel lane: measures how long the local model needs for a window and for a write of the maximum number of windows
/// </summary>
[TestFixture]
public class LocalModelLatencyTests {
    const int WarmupRuns = 3;
    const int TimedRuns = 15;

    static string TextOfLength(int length) {
        StringBuilder text = new();
        for (int i = 0; text.Length < length; i++)
            text.Append("Sentence number ").Append(i).Append(" describes how the scheduler hands queued jobs to idle workers. ");
        return text.ToString()[..length];
    }

    static double Percentile(double[] sorted, double fraction) => sorted[(int)Math.Ceiling(fraction * sorted.Length) - 1];


    [Test]
    [NonParallelizable]
    [Explicit("LocalModel lane - requires DIVOID_EMBEDDING_MODEL_DIR and POSTGRES_CONNECTION; runs on the gangolf Docker daemon only.")]
    [Category("LocalModel")]
    [Description("Reports p50 and p95 per window length and the wall time of a write of the maximum number of windows; the numbers are measured, not asserted.")]
    public async Task Embed_WindowLengths_ReportsLatency() {
        await using ServiceProvider provider = await LocalModelEnvironment.PreparedProvider();
        ILocalEmbeddingModel model = provider.GetRequiredService<ILocalEmbeddingModel>();
        foreach (int length in new[] { 100, 1000, 3000, 7000, 8000 }) {
            string prompt = EmbeddingPrompt.For(TextOfLength(length), EmbeddingPurpose.Document);
            for (int i = 0; i < WarmupRuns; i++)
                model.Embed(prompt);
            double[] milliseconds = Enumerable.Range(0, TimedRuns).Select(_ => {
                Stopwatch watch = Stopwatch.StartNew();
                model.Embed(prompt);
                return watch.Elapsed.TotalMilliseconds;
            }).Order().ToArray();
            TestContext.Progress.WriteLine($"latency window_chars={length} p50_ms={Percentile(milliseconds, 0.5):F0} p95_ms={Percentile(milliseconds, 0.95):F0}");
        }

        using PgvectorBareDatabase database = new();
        await PgvectorBareDatabase.Start(database.AdminEntities);
        NodeService service = new(database.AdminEntities, new EmbeddingCapability(true), provider.GetRequiredService<IEmbeddingProducer>());
        long id = (await service.CreateNode(new NodeDetails { Type = "documentation", Name = "maximum windows" }, callerId: 0)).Id;
        byte[] body = Encoding.UTF8.GetBytes(TextOfLength(300000));
        string[] windows = EmbeddingInputComposer.ComposeWindows("maximum windows", body, "text/markdown");
        Stopwatch write = Stopwatch.StartNew();
        await service.UploadContent(id, "text/markdown", new MemoryStream(body), callerId: 0, isAdmin: true);

        TestContext.Progress.WriteLine($"latency write windows={windows.Length} total_s={write.Elapsed.TotalSeconds:F1}");
        Assert.That(windows, Has.Length.EqualTo(32), "the node must reach the maximum number of windows");
    }
}
