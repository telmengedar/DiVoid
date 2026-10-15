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

    static readonly string[][] Needs = [
        [Topics[0].Paraphrase, "reclaiming disk space taken by outdated row versions in a SQL server", "preventing table bloat caused by deleted and updated records"],
        [Topics[1].Paraphrase, "baking a loaf from a fermented flour and water culture", "how long should a natural leaven ferment before the dough is shaped"]
    ];

    static async Task<Dictionary<long, double>> SimilaritiesOf(NodeService service, string query) {
        NodeDetails[] ranked = await NodeSearch.Ranked(service, new NodeFilter { Query = query, Count = 10 });
        return ranked.ToDictionary(node => node.Id, node => (double) node.Similarity!.Value);
    }

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
    [Description("On plain pgvector without the embedding function, paraphrased queries rank their node first, three phrasings of one need sent together rank that node first with the mean of their similarities, and the tail of a windowed node is found.")]
    public async Task ListPaged_PlainPostgresWithLocalModel_RanksSourceNodeFirst() {
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
        NodeDetails[][] needs = new NodeDetails[Needs.Length][];
        Dictionary<long, double>[][] needSingles = new Dictionary<long, double>[Needs.Length][];
        for (int need = 0; need < Needs.Length; need++) {
            needs[need] = await NodeSearch.Ranked(service, new NodeFilter { Queries = Needs[need], Count = 10 });
            needSingles[need] = new Dictionary<long, double>[Needs[need].Length];
            for (int phrasing = 0; phrasing < Needs[need].Length; phrasing++)
                needSingles[need][phrasing] = await SimilaritiesOf(service, Needs[need][phrasing]);
        }
        NodeDetails[] all = await NodeSearch.Ranked(service, new NodeFilter { Queries = Topics.Select(topic => topic.Paraphrase).ToArray(), Count = 10 });
        TestContext.Progress.WriteLine("five-query ranking: " + string.Join(", ", all.Select(node => $"{node.Name}={node.Similarity:F3}")));
        long[] tail = await NodeSearch.RankedIds(service, new NodeFilter { Query = longContent[^300..], Count = 10 });

        Assert.Multiple(() => {
            Assert.That(database.Scalar<long>("SELECT count(*) FROM pg_proc WHERE proname = 'embedding'"), Is.Zero);
            Assert.That(database.Scalar<long>("SELECT count(*) FROM node WHERE vector_dims(embedding) = 768"), Is.EqualTo(Topics.Length + 1));
            for (int i = 0; i < Topics.Length; i++)
                Assert.That(single[i][0], Is.EqualTo(ids[i]), Topics[i].Name);
            for (int need = 0; need < Needs.Length; need++) {
                Assert.That(needs[need][0].Id, Is.EqualTo(ids[need]), $"three phrasings of the need of {Topics[need].Name}");
                Assert.That(needs[need], Has.Length.EqualTo(Topics.Length + 1), "every node is listed");
                foreach (NodeDetails node in needs[need])
                    Assert.That((double) node.Similarity!.Value, Is.EqualTo(needSingles[need].Average(single => single[node.Id])).Within(1e-5), $"mean similarity of {node.Name} for the need of {Topics[need].Name}");
            }
            Assert.That(tail.Take(3), Does.Contain(longId), "tail passage of the windowed node");
        });
    }
}
