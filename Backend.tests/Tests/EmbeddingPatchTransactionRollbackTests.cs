using System;
using System.Threading;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Patches;

namespace Backend.tests.Tests;

/// <summary>
/// pins that a failing embedding write rolls back the name patch it belongs to
/// </summary>
[TestFixture, Parallelizable]
public class EmbeddingPatchTransactionRollbackTests
{
    static readonly IEmbeddingCapability DisabledCapability = new EmbeddingCapability(false);
    static readonly IEmbeddingCapability EnabledCapability = new EmbeddingCapability(true);


    /// <summary>
    /// a name patch whose embedding write throws leaves the old name in place
    /// </summary>
    [Test, Parallelizable]
    public async Task Patch_EmbeddingThrowsMidTransaction_NameUpdateRolledBack()
    {
        using DatabaseFixture fixture = new();
        NodeService seedSvc = new(fixture.EntityManager, DisabledCapability);
        NodeService patchSvc = new(fixture.EntityManager, EnabledCapability);

        NodeDetails node = await seedSvc.CreateNode(new NodeDetails { Type = "task", Name = "Original" }, callerId: 0);

        Exception? thrown = null;
        try
        {
            await patchSvc.Patch(
                node.Id,
                [new PatchOperation { Op = "replace", Path = "/name", Value = "New" }],
                callerId: 0, isAdmin: true, CancellationToken.None);
        }
        catch (ArgumentException ex) when (ex.Message.Contains("cast target type"))
        {
            thrown = ex;
        }

        Node live = await fixture.EntityManager.Load<Node>()
                                               .Where(n => n.Id == node.Id)
                                               .ExecuteEntityAsync();

        Assert.Multiple(() => {
            Assert.That(thrown, Is.Not.Null,
                "embedding branch on SQLite must throw (the vector cast is Postgres-only) — if null the capability guard is not entering the embedding branch");
            Assert.That(live.Name, Is.EqualTo("Original"),
                "UPDATE 1 (name) must be rolled back: transaction must not commit when the embedding step throws");
            Assert.That(live.Embedding, Is.Null,
                "no partial embedding write: exception before any embedding SET executes, so embedding must remain null");
        });
    }


    /// <summary>
    /// the embedding write fails with the unsupported vector cast error
    /// </summary>
    [Test, Parallelizable]
    public async Task Patch_EmbeddingThrowsMidTransaction_ExceptionNamesUnsupportedVectorCast()
    {
        using DatabaseFixture fixture = new();
        NodeService seedSvc = new(fixture.EntityManager, DisabledCapability);
        NodeService patchSvc = new(fixture.EntityManager, EnabledCapability);

        NodeDetails node = await seedSvc.CreateNode(new NodeDetails { Type = "documentation", Name = "OriginalDoc" }, callerId: 0);

        Exception? thrown = null;
        try
        {
            await patchSvc.Patch(
                node.Id,
                [new PatchOperation { Op = "replace", Path = "/name", Value = "NewDoc" }],
                callerId: 0, isAdmin: true, CancellationToken.None);
        }
        catch (ArgumentException ex) when (ex.Message.Contains("cast target type"))
        {
            thrown = ex;
        }

        Assert.That(thrown, Is.Not.Null,
            "exception must be thrown from the embedding branch — capability enabled on SQLite triggers the Postgres-only vector cast");
        Assert.That(thrown!.Message, Does.Contain("cast target type").IgnoreCase,
            "exception message must name the unsupported vector cast — distinguishes embedding-branch throw from unrelated failures");
    }


    /// <summary>
    /// non-name PATCH does not enter the embedding branch regardless of the capability flag —
    /// confirming the fault in the rollback test is caused by the embedding path, not by some
    /// other transaction issue.
    /// </summary>
    [Test, Parallelizable]
    public async Task Patch_NonNameField_EmbeddingCapabilityEnabled_NoThrow()
    {
        using DatabaseFixture fixture = new();
        NodeService seedSvc = new(fixture.EntityManager, DisabledCapability);
        NodeService patchSvc = new(fixture.EntityManager, EnabledCapability);

        NodeDetails node = await seedSvc.CreateNode(
            new NodeDetails { Type = "task", Name = "StableNode", Status = "open" }, callerId: 0);

        NodeDetails patched = await patchSvc.Patch(
            node.Id,
            [new PatchOperation { Op = "replace", Path = "/status", Value = "closed" }],
            callerId: 0, isAdmin: true, CancellationToken.None);

        Assert.That(patched.Status, Is.EqualTo("closed"),
            "non-name PATCH must succeed even with capability enabled — the embedding branch is only entered when the name is touched");
    }
}
