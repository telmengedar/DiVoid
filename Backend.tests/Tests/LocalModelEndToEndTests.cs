using System.Text;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// LocalModel lane: writes and searches nodes on plain Postgres with pgvector and the real local model, with no embedding function and no external provider
/// </summary>
[TestFixture, Parallelizable]
public class LocalModelEndToEndTests {
    static readonly (string Name, string Content, string Paraphrase)[] Topics = [
        ("Postgres vacuum tuning", "Autovacuum removes dead row versions so that tables and indexes do not bloat. Lower the scale factor on busy tables and watch the dead tuple counters.", "how do I keep my relational database from filling up with obsolete rows"),
        ("Sourdough baking schedule", "Feed the starter twelve hours before mixing, autolyse the flour and water, fold the dough four times and proof it overnight in the fridge.", "making bread with wild yeast over a couple of days"),
        ("Ingress TLS certificates", "Cert manager requests certificates from the issuer and stores them in a secret which the ingress controller mounts to terminate HTTPS.", "encrypting traffic to a cluster with automatically renewed keys"),
        ("Marathon training plan", "Build the weekly mileage slowly, keep one long run on Sundays, add tempo intervals on Wednesdays and taper for the final two weeks.", "preparing my legs for a forty two kilometre race"),
        ("Medieval castle architecture", "Concentric walls, a gatehouse with a portcullis, arrow slits in the curtain wall and a keep on the motte defended the stronghold.", "fortified buildings where knights lived and sieges were endured")
    ];

    static string LongContent() {
        string[] paragraphs = [
            "Coral reefs grow where shallow tropical water is clear and warm, because the algae living inside the polyps need sunlight to photosynthesise.",
            "Parrotfish graze on the algae that would otherwise smother the corals, and their droppings become the white sand of the nearby beaches.",
            "Bleaching happens when heat stress makes the polyps expel their algae, leaving the white skeleton exposed and the colony starving.",
            "Reef fish nurseries inside mangrove roots supply the adult populations, which is why coastal clearing damages fisheries far offshore."
        ];
        StringBuilder text = new();
        for (int i = 0; text.Length < 16000; i++)
            text.Append(paragraphs[i % paragraphs.Length]).Append(" Observation ").Append(i).Append(" of the survey transect.\n\n");
        text.Append("At the end of the survey the team compared the surviving colonies with the satellite temperature record and found that every colony which had bleached sat inside a lagoon where the water barely circulated and the midday heat accumulated.");
        return text.ToString();
    }


    [Test, Parallelizable]
    [Explicit("LocalModel lane - requires DIVOID_EMBEDDING_MODEL_DIR and POSTGRES_CONNECTION; runs on the gangolf Docker daemon only.")]
    [Category("LocalModel")]
    [Description("On plain pgvector without the embedding function, paraphrased queries rank their node first, a multi-query of two topics finds both nodes, and the tail of a windowed node is found.")]
    public async Task PlainPostgres_WritesAndSearches_RankSourceNodeFirst() {
        using PgvectorBareDatabase database = new();
        await PgvectorBareDatabase.Start(database.AdminEntities);
        await using ServiceProvider provider = await LocalModelEnvironment.PreparedProvider();
        NodeService service = new(database.AdminEntities, new EmbeddingCapability(true), provider.GetRequiredService<IEmbeddingProducer>());
        long[] ids = new long[Topics.Length];
        for (int i = 0; i < Topics.Length; i++) {
            ids[i] = (await service.CreateNode(new NodeDetails { Type = "documentation", Name = Topics[i].Name }, callerId: 0)).Id;
            await service.UploadContent(ids[i], "text/markdown", new MemoryStream(Encoding.UTF8.GetBytes(Topics[i].Content)), callerId: 0, isAdmin: true);
        }
        string longContent = LongContent();
        long longId = (await service.CreateNode(new NodeDetails { Type = "documentation", Name = "Coral reef survey" }, callerId: 0)).Id;
        await service.UploadContent(longId, "text/markdown", new MemoryStream(Encoding.UTF8.GetBytes(longContent)), callerId: 0, isAdmin: true);

        long[][] single = new long[Topics.Length][];
        for (int i = 0; i < Topics.Length; i++)
            single[i] = await NodeSearch.RankedIds(service, new NodeFilter { Query = Topics[i].Paraphrase, Count = 10 });
        long[] pair = await NodeSearch.RankedIds(service, new NodeFilter { Queries = [Topics[0].Paraphrase, Topics[1].Paraphrase], Count = 10 });
        NodeDetails[] all = await NodeSearch.Ranked(service, new NodeFilter { Queries = Topics.Select(topic => topic.Paraphrase).ToArray(), Count = 10 });
        TestContext.Progress.WriteLine("five-query ranking: " + string.Join(", ", all.Select(node => $"{node.Name}={node.Similarity:F3}")));
        long[] tail = await NodeSearch.RankedIds(service, new NodeFilter { Query = longContent[^300..], Count = 10 });

        Assert.Multiple(() => {
            Assert.That(database.Scalar<long>("SELECT count(*) FROM pg_proc WHERE proname = 'embedding'"), Is.Zero);
            Assert.That(database.Scalar<long>("SELECT count(*) FROM node WHERE vector_dims(embedding) = 768"), Is.EqualTo(Topics.Length + 1));
            for (int i = 0; i < Topics.Length; i++)
                Assert.That(single[i][0], Is.EqualTo(ids[i]), Topics[i].Name);
            Assert.That(pair.Take(2), Is.EquivalentTo(new[] { ids[0], ids[1] }), "multi query of two topics");
            Assert.That(tail.Take(3), Does.Contain(longId), "tail passage of the windowed node");
        });
    }
}
