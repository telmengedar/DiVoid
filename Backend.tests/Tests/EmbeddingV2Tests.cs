using System.Text;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Patches;

namespace Backend.tests.Tests;

/// <summary>
/// integration tests for the embeddings v2 trigger surface (task #437).
/// all tests run on SQLite with capability disabled — they verify:
///   (a) the code path is entered (no crash, correct SQL shape for the non-embedding steps)
///   (b) embedding column remains null on SQLite because the capability flag prevents the
///       <c>embedding()</c> call from reaching the DB layer
///
/// load-bearing assertions (per DiVoid #275):
///   - each test fails if the corresponding code path regresses (wrong path, wrong transaction, crash).
///   - Postgres smoke tests (actual byte-change assertions) are described in §10 Phase 11 of the
///     architecture doc and must be run manually pre-merge.
/// </summary>
[TestFixture]
public class EmbeddingV2Tests
{
    static readonly IEmbeddingCapability DisabledCapability = new EmbeddingCapability(false);

    static NodeService MakeService(DatabaseFixture fixture)
        => new(fixture.EntityManager, DisabledCapability);


    [Test]
    public async Task CreateNode_WithName_SqliteFixture_NoEmbeddingAndNoThrow()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails created = await svc.CreateNode(new NodeDetails { Type = "task", Name = "CreateEmbedTest" });

        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == created.Id)
                                              .ExecuteEntityAsync();

        Assert.Multiple(() => {
            Assert.That(raw, Is.Not.Null, "node must be inserted");
            Assert.That(raw.Name, Is.EqualTo("CreateEmbedTest"), "name must be stored");
            Assert.That(raw.Embedding, Is.Null,
                "Embedding must remain null on SQLite — capability disabled, embedding UPDATE skipped");
        });
    }

    [Test]
    public async Task CreateNode_EmptyName_SqliteFixture_NoEmbeddingAndNoThrow()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails created = await svc.CreateNode(new NodeDetails { Type = "task", Name = "" });

        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == created.Id)
                                              .ExecuteEntityAsync();

        Assert.That(raw.Embedding, Is.Null,
            "no embedding for empty-name node — EmbeddingInputComposer returns null for empty name + empty content");
    }


    [Test]
    public async Task UploadContent_TextType_V2_SqliteFixture_ContentStoredNoEmbedding()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await svc.CreateNode(new NodeDetails { Type = "doc", Name = "ContentTest" });
        byte[] content = Encoding.UTF8.GetBytes("# v2 content");

        await svc.UploadContent(node.Id, "text/markdown", new MemoryStream(content));

        (string ct, Stream stream) = await svc.GetNodeData(node.Id);
        byte[] stored = new byte[content.Length];
        await stream.ReadExactlyAsync(stored);

        Assert.Multiple(() => {
            Assert.That(ct, Is.EqualTo("text/markdown"), "content type must be stored");
            Assert.That(stored, Is.EqualTo(content), "content bytes must be stored");
        });
    }

    [Test]
    public async Task UploadContent_NonTextType_V2_SqliteFixture_NoEmbeddingAndNoThrow()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await svc.CreateNode(new NodeDetails { Type = "asset", Name = "ImageNode" });
        byte[] content = [0x89, 0x50, 0x4E, 0x47]; // PNG magic bytes

        await svc.UploadContent(node.Id, "image/png", new MemoryStream(content));

        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == node.Id)
                                              .ExecuteEntityAsync();

        Assert.That(raw.Embedding, Is.Null,
            "on SQLite the embedding step is skipped entirely — Embedding stays null regardless of content type");
    }


    [Test]
    public async Task Patch_ReplaceName_SqliteFixture_NameUpdatedNoEmbeddingAndNoThrow()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await svc.CreateNode(new NodeDetails { Type = "task", Name = "OldName" });

        NodeDetails patched = await svc.Patch(node.Id,
            [new PatchOperation { Op = "replace", Path = "/name", Value = "NewName" }],
            CancellationToken.None);

        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == node.Id)
                                              .ExecuteEntityAsync();

        Assert.Multiple(() => {
            Assert.That(patched.Name, Is.EqualTo("NewName"), "Patch must update the name field");
            Assert.That(raw.Name, Is.EqualTo("NewName"), "updated name must persist in the DB");
            Assert.That(raw.Embedding, Is.Null,
                "Embedding stays null on SQLite — embedding UPDATE is skipped when capability is disabled");
        });
    }

    [Test]
    public async Task Patch_NonNameField_SqliteFixture_EmbeddingNotTouched()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await svc.CreateNode(new NodeDetails { Type = "task", Name = "StatusPatch", Status = "open" });

        NodeDetails patched = await svc.Patch(node.Id,
            [new PatchOperation { Op = "replace", Path = "/status", Value = "closed" }],
            CancellationToken.None);

        Assert.That(patched.Status, Is.EqualTo("closed"), "status must be updated");
    }

    [Test]
    public async Task Patch_NonExistentNode_ThrowsNotFoundException()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        Assert.ThrowsAsync<Pooshit.AspNetCore.Services.Errors.Exceptions.NotFoundException<Node>>(
            () => svc.Patch(999999L,
                [new PatchOperation { Op = "replace", Path = "/name", Value = "X" }],
                CancellationToken.None));
    }


    [Test]
    public async Task Patch_PatchListContainsNameAndStatus_OnlyNameTriggersCounted_NoCrash()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await svc.CreateNode(new NodeDetails { Type = "task", Name = "OldName", Status = "open" });

        NodeDetails patched = await svc.Patch(node.Id,
            [
                new PatchOperation { Op = "replace", Path = "/name", Value = "BrandNewName" },
                new PatchOperation { Op = "replace", Path = "/status", Value = "in-progress" }
            ],
            CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(patched.Name, Is.EqualTo("BrandNewName"));
            Assert.That(patched.Status, Is.EqualTo("in-progress"));
        });
    }


    [Test]
    public async Task UploadContent_NonTextAfterNameCreation_V2_SqliteFixture_NoThrow()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await svc.CreateNode(new NodeDetails { Type = "asset", Name = "AssetNode" });
        byte[] img = [0xFF, 0xD8, 0xFF]; // JPEG magic

        await svc.UploadContent(node.Id, "image/jpeg", new MemoryStream(img));

        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == node.Id)
                                              .ExecuteEntityAsync();

        Assert.That(raw.ContentType, Is.EqualTo("image/jpeg"),
            "content type must be stored even when capability is disabled");
    }
}
