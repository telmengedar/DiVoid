using System.Text;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Patches;

namespace Backend.tests.Tests;

/// <summary>
/// manual-lane Postgres test of the value path: vectors computed in .NET are written and searched on plain pgvector without the embedding function
/// </summary>
[TestFixture, Parallelizable]
public class ValueProducerPostgresManualTests {
    const string EmbeddingFunctionCount = "SELECT count(*) FROM pg_proc WHERE proname = 'embedding'";

    static string Filler(string prefix, int words) => string.Concat(Enumerable.Range(0, words).Select(i => $"{prefix}{i} "));

    static async Task<NodeService> StartedService(PgvectorBareDatabase database) {
        await PgvectorBareDatabase.Start(database.AdminEntities);
        return new NodeService(database.AdminEntities, new EmbeddingCapability(true), new HashedWordEmbeddingProducer());
    }


    [Test, Parallelizable]
    [Explicit("Manual Postgres test - requires POSTGRES_CONNECTION to a superuser connection with the pgvector extension available.")]
    [Category("PostgresManual")]
    [Description("Constant vectors bind through Ocelot and Npgsql, combine in SQL for windowed and multi-query searches, and rank on a database without the embedding function.")]
    public async Task ListPaged_ConstantVectorsOnPlainPgvector_RanksWithoutEmbeddingFunction() {
        using PgvectorBareDatabase database = new();
        NodeService service = await StartedService(database);
        byte[] longBody = Encoding.UTF8.GetBytes(Filler("tailword", 3000) + "fjord glacier avalanche");

        NodeDetails created = await service.CreateNode(new NodeDetails { Type = "documentation", Name = "kubernetes ingress controller" }, callerId: 0);
        NodeDetails uploaded = await service.CreateNode(new NodeDetails { Type = "documentation", Name = "placeholder" }, callerId: 0);
        await service.UploadContent(uploaded.Id, "text/markdown", new MemoryStream("sourdough starter hydration ratio"u8.ToArray()), callerId: 0, isAdmin: true);
        NodeDetails renamed = await service.CreateNode(new NodeDetails { Type = "documentation", Name = "draft" }, callerId: 0);
        await service.Patch(renamed.Id, [new PatchOperation { Op = "replace", Path = "/name", Value = "baroque harpsichord tuning" }], callerId: 0, isAdmin: true, CancellationToken.None);
        NodeDetails windowed = await service.CreateNode(new NodeDetails { Type = "documentation", Name = "long note" }, callerId: 0);
        await service.UploadContent(windowed.Id, "text/markdown", new MemoryStream(longBody), callerId: 0, isAdmin: true);

        long[] byName = await NodeSearch.RankedIds(service, new NodeFilter { Query = "kubernetes ingress", Count = 10 });
        long[] byContent = await NodeSearch.RankedIds(service, new NodeFilter { Query = "sourdough hydration", Count = 10 });
        long[] byRename = await NodeSearch.RankedIds(service, new NodeFilter { Query = "harpsichord", Count = 10 });
        long[] byTail = await NodeSearch.RankedIds(service, new NodeFilter { Query = "fjord glacier avalanche", Count = 10 });
        long[] multi = await NodeSearch.RankedIds(service, new NodeFilter { Queries = ["sourdough", "harpsichord"], Count = 10 });

        Assert.Multiple(() => {
            Assert.That(database.Scalar<long>(EmbeddingFunctionCount), Is.Zero, "the database must not have an embedding function");
            Assert.That(database.Scalar<long>("SELECT count(*) FROM node WHERE vector_dims(embedding) = 768"), Is.EqualTo(4));
            Assert.That(byName[0], Is.EqualTo(created.Id), "name written on create");
            Assert.That(byContent[0], Is.EqualTo(uploaded.Id), "content written on upload");
            Assert.That(byRename[0], Is.EqualTo(renamed.Id), "name written on patch");
            Assert.That(byTail[0], Is.EqualTo(windowed.Id), "words of the last window found through the combined vector");
            Assert.That(multi.Take(2), Is.EquivalentTo(new[] { uploaded.Id, renamed.Id }), "both sums of a multi query");
        });
    }


    [Test, Parallelizable]
    [Explicit("Manual Postgres test - requires POSTGRES_CONNECTION to a superuser connection with the pgvector extension available.")]
    [Category("PostgresManual")]
    [Description("A short node stores exactly the components the producer computed, and a windowed node stores a unit vector.")]
    public async Task Write_ConstantVector_StoresProducerComponentsExactly() {
        using PgvectorBareDatabase database = new();
        NodeService service = await StartedService(database);
        string name = "stored exactly check";
        NodeDetails short1 = await service.CreateNode(new NodeDetails { Type = "documentation", Name = name }, callerId: 0);
        NodeDetails windowed = await service.CreateNode(new NodeDetails { Type = "documentation", Name = "long" }, callerId: 0);
        await service.UploadContent(windowed.Id, "text/markdown", new MemoryStream(Encoding.UTF8.GetBytes(Filler("w", 4000))), callerId: 0, isAdmin: true);

        string stored = database.Scalar<string>($"SELECT embedding::text FROM node WHERE id = {short1.Id}");
        float[] actual = stored.Trim('[', ']').Split(',').Select(component => float.Parse(component, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        double norm = database.Scalar<double>($"SELECT vector_norm(embedding) FROM node WHERE id = {windowed.Id}");

        Assert.Multiple(() => {
            Assert.That(actual, Is.EqualTo(HashedWordEmbeddingProducer.Vector(name)).Within(1e-6));
            Assert.That(norm, Is.EqualTo(1.0).Within(1e-4));
        });
    }
}
