using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.Init;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Npgsql;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Info;

namespace Backend.tests.Tests;

/// <summary>
/// manual-lane Postgres smoke test for the linkedto filter driven through
/// <see cref="NodeService.ListPaged"/>.
/// </summary>
[TestFixture]
public class LinkedToPostgresManualTests
{
    static readonly IEmbeddingCapability DisabledCapability = new EmbeddingCapability(false);

    static IEntityManager CreatePostgresManager(string connString)
    {
        IDBClient client = ClientFactory.Create(() => new NpgsqlConnection(connString), new PostgreInfo(), true);
        return new EntityManager(client);
    }

    static async Task ApplySchema(IEntityManager em)
    {
        DatabaseModelService svc = new(em);
        await svc.StartAsync(CancellationToken.None);
    }

    static NodeService MakeService(IEntityManager em) => new(em, DisabledCapability);

    static async Task<NodeDetails> Create(NodeService svc, string name)
        => await svc.CreateNode(new NodeDetails { Type = "task", Name = name }, callerId: 0);

    static async Task<List<NodeDetails>> CollectPage(AsyncPageResponseWriter<NodeDetails> writer)
    {
        byte[] buffer;
        using (MemoryStream ms = new())
        {
            await writer.Write(ms);
            buffer = ms.ToArray();
        }
        using MemoryStream readStream = new(buffer);
        string json = await new StreamReader(readStream).ReadToEndAsync();
        Pooshit.AspNetCore.Services.Data.Page<NodeDetails> page =
            Pooshit.Json.Json.Read<Pooshit.AspNetCore.Services.Data.Page<NodeDetails>>(json);
        return page.Result?.ToList() ?? [];
    }

    static void PurgeTestData(IEntityManager em)
    {
        em.Delete<NodeLink>().Execute();
        em.Delete<Node>().Execute();
    }

    [Test]
    [Explicit("Manual Postgres smoke test — requires POSTGRES_CONNECTION and a running Postgres instance.")]
    [Category("PostgresManual")]
    [Description("Not parallelizable — PurgeTestData deletes all Node/NodeLink rows in the shared Postgres connection.")]
    public async Task LinkedTo_Postgres_FindsNeighbourExcludesSeed()
    {
        string connString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")!;

        IEntityManager em = CreatePostgresManager(connString);
        await ApplySchema(em);
        PurgeTestData(em);

        NodeService svc = MakeService(em);

        NodeDetails a = await Create(svc, "NodeA");
        NodeDetails b = await Create(svc, "NodeB");
        NodeDetails c = await Create(svc, "NodeC");

        await svc.LinkNodes(a.Id, b.Id, callerId: 0, isAdmin: true);

        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPaged(
            new NodeFilter { LinkedTo = [a.Id], Count = 100 }, callerId: 0, isAdmin: true);
        List<NodeDetails> results = await CollectPage(writer);

        long[] ids = results.Select(n => n.Id).ToArray();
        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(b.Id),
                "B is linked to A — must appear when LinkedTo=A");
            Assert.That(ids, Does.Not.Contain(a.Id),
                "A is the seed — must be excluded from results");
            Assert.That(ids, Does.Not.Contain(c.Id),
                "C has no link to A — must not appear");
        });
    }
}
