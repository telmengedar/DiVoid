using System.IO;
using Backend.tests.Fixtures;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.Json;

namespace Backend.tests.Tests;

/// <summary>
/// manual-lane Postgres test of the id and name exclusions against a ranked page on a real pgvector column
/// </summary>
[TestFixture, Parallelizable]
public class ExcludePostgresManualTests
{
    static async Task<long[]> RankedIds(NodeService service, NodeFilter filter)
    {
        AsyncPageResponseWriter<NodeDetails> writer = await service.ListPaged(filter, callerId: 0, isAdmin: true);
        using MemoryStream stream = new();
        await writer.Write(stream);
        Page<NodeDetails> page = Json.Read<Page<NodeDetails>>(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        return page.Result.Select(n => n.Id).ToArray();
    }

    [Test, Parallelizable]
    [Explicit("Manual Postgres test - requires POSTGRES_CONNECTION to a superuser connection with the pgvector extension available.")]
    [Category("PostgresManual")]
    [Description("DiVoid #13604: a ranked page is filled with eligible rows when the better-ranked rows are excluded")]
    public async Task ExcludeName_Postgres_RankedPageFilledWithEligibleRows()
    {
        using EmbeddingPostgresDatabase database = new();
        await database.Start();
        NodeService service = new(database.Entities, new EmbeddingCapability(true));
        const string probe = "processor-run-probe";
        long[] excluded = new long[3];
        for (int i = 0; i < excluded.Length; i++)
            excluded[i] = (await service.CreateNode(new NodeDetails { Type = "session-log", Name = probe }, callerId: 0)).Id;
        long[] eligible = new long[5];
        for (int i = 0; i < eligible.Length; i++)
            eligible[i] = (await service.CreateNode(new NodeDetails { Type = "session-log", Name = $"eligible-{i}" }, callerId: 0)).Id;

        long[] control = await RankedIds(service, new() { Query = probe, Count = 3 });
        long[] filtered = await RankedIds(service, new() { Query = probe, Count = 3, ExcludeName = ["processor-run-%"] });

        Assert.Multiple(() => {
            Assert.That(control, Is.EquivalentTo(excluded), "control: the nodes to be excluded must outrank the eligible ones");
            Assert.That(filtered, Has.Length.EqualTo(3));
            Assert.That(filtered, Is.SubsetOf(eligible));
        });
    }
}
