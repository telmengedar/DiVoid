using System.Text;
using Backend.tests.Fixtures;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Patches;

namespace Backend.tests.Tests;

/// <summary>
/// manual-lane Postgres test of the windowed embedding writes against a real pgvector column
/// </summary>
[TestFixture, Parallelizable]
public class EmbeddingWindowsPostgresManualTests
{
    const string OfTextSql = "subvector(embedding(@model, @text)::vector, 1, 768)";

    static bool EqualsOfText(EmbeddingPostgresDatabase database, long id, string text)
    {
        return database.Scalar<bool>($"SELECT embedding = {OfTextSql} FROM node WHERE id = @id",
                                     ("id", id), ("model", TextContentTypePredicate.EmbeddingModel), ("text", text));
    }

    [Test, Parallelizable]
    [Explicit("Manual Postgres test - requires POSTGRES_CONNECTION to a superuser connection with the pgvector extension available.")]
    [Category("PostgresManual")]
    [Description("DiVoid #16207: a node whose composed input fits stores exactly the single-text vector on create, upload, content patch, name patch and backfill")]
    public async Task ShortNode_EachWritePath_StoresVectorIdenticalToOfTextOfCompose()
    {
        using EmbeddingPostgresDatabase database = new();
        await database.Start();
        NodeService service = new(database.Entities, new EmbeddingCapability(true));
        EmbeddingBackfillService backfill = new(database.Entities, new EmbeddingCapability(true), NullLogger<EmbeddingBackfillService>.Instance);
        byte[] body = "short body"u8.ToArray();

        NodeDetails node = await service.CreateNode(new NodeDetails { Type = "documentation", Name = "short node" }, callerId: 0);
        bool afterCreate = EqualsOfText(database, node.Id, "short node");
        await service.UploadContent(node.Id, "text/markdown", new MemoryStream(body), callerId: 0, isAdmin: true);
        bool afterUpload = EqualsOfText(database, node.Id, EmbeddingInputComposer.Compose("short node", body, "text/markdown"));
        await service.PatchContent(node.Id, [new ContentEdit { Unit = ContentEditUnit.Char, Start = 0, Length = 5, Value = "tiny" }], callerId: 0, isAdmin: true, CancellationToken.None);
        bool afterContentPatch = EqualsOfText(database, node.Id, "short node\n\ntiny body");
        await service.Patch(node.Id, [new PatchOperation { Op = "replace", Path = "/name", Value = "renamed node" }], callerId: 0, isAdmin: true, CancellationToken.None);
        bool afterNamePatch = EqualsOfText(database, node.Id, "renamed node\n\ntiny body");
        await backfill.RunAsync();
        bool afterBackfill = EqualsOfText(database, node.Id, "renamed node\n\ntiny body");

        Assert.Multiple(() => {
            Assert.That(afterCreate, Is.True, "create");
            Assert.That(afterUpload, Is.True, "upload");
            Assert.That(afterContentPatch, Is.True, "content patch");
            Assert.That(afterNamePatch, Is.True, "name patch");
            Assert.That(afterBackfill, Is.True, "backfill");
        });
    }

    [Test, Parallelizable]
    [Explicit("Manual Postgres test - requires POSTGRES_CONNECTION to a superuser connection with the pgvector extension available.")]
    [Category("PostgresManual")]
    [Description("DiVoid #16207: a long node stores a unit-norm vector pointing along the mean of its normalised window vectors")]
    public async Task LongNode_StoresUnitNormCentroid()
    {
        using EmbeddingPostgresDatabase database = new();
        await database.Start();
        NodeService service = new(database.Entities, new EmbeddingCapability(true));
        byte[] body = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 5000).Select(i => $"w{i} "))[..20000]);
        string[] windows = EmbeddingInputComposer.ComposeWindows("long node", body, "text/markdown");

        NodeDetails node = await service.CreateNode(new NodeDetails { Type = "documentation", Name = "long node" }, callerId: 0);
        await service.UploadContent(node.Id, "text/markdown", new MemoryStream(body), callerId: 0, isAdmin: true);

        double norm = database.Scalar<double>("SELECT vector_norm(embedding) FROM node WHERE id = @id", ("id", node.Id));
        double cosine = database.Scalar<double>("SELECT 1 - (n.embedding <=> (SELECT avg(l2_normalize(subvector(embedding(@model, w)::vector, 1, 768))) FROM unnest(@windows) w)) FROM node n WHERE n.id = @id",
                                                ("id", node.Id), ("model", TextContentTypePredicate.EmbeddingModel), ("windows", windows));
        Assert.Multiple(() => {
            Assert.That(windows, Has.Length.EqualTo(5));
            Assert.That(norm, Is.EqualTo(1.0).Within(1e-4));
            Assert.That(cosine, Is.EqualTo(1.0).Within(1e-4));
        });
    }
}
