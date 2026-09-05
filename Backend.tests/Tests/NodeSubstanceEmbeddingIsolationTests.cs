using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Errors;

namespace Backend.tests.Tests;

/// <summary>
/// Branch-level tests for the interaction between a node substance and the embedding-regeneration path.
/// </summary>
[TestFixture, Parallelizable]
public class NodeSubstanceEmbeddingIsolationTests
{
    static readonly IEmbeddingCapability DisabledCapability = new EmbeddingCapability(false);
    static readonly IEmbeddingCapability EnabledCapability = new EmbeddingCapability(true);

    [Test, Parallelizable]
    [Description("S9 — invariant I3: with embeddings enabled a name PATCH enters the Postgres-only regeneration branch and throws on SQLite, while a substance PATCH does not enter it.")]
    public async Task Patch_ReplaceSubstance_LeavesEmbeddingUntouched()
    {
        using DatabaseFixture fixture = new();
        NodeService seedSvc = new(fixture.EntityManager, DisabledCapability);
        NodeService patchSvc = new(fixture.EntityManager, EnabledCapability);

        NodeDetails node = await seedSvc.CreateNode(
            new NodeDetails { Type = "documentation", Name = "S9_Subject", Substance = "S9|before" },
            callerId: 0);

        Exception? nameThrown = null;
        try
        {
            await patchSvc.Patch(
                node.Id,
                [new PatchOperation { Op = "replace", Path = "/name", Value = "S9_Renamed" }],
                callerId: 0, isAdmin: true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            nameThrown = ex;
        }

        Exception? substanceThrown = null;
        try
        {
            await patchSvc.Patch(
                node.Id,
                [new PatchOperation { Op = "replace", Path = "/substance", Value = "S9|after" }],
                callerId: 0, isAdmin: true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            substanceThrown = ex;
        }

        Node live = await fixture.EntityManager.Load<Node>()
                                               .Where(n => n.Id == node.Id)
                                               .ExecuteEntityAsync();

        Assert.Multiple(() => {
            Assert.That(nameThrown, Is.Not.Null,
                "scenario check: a name PATCH must reach the Postgres-only embedding branch and throw on SQLite — "
                + "null here means the embedding path is not live in this fixture and the assertion below proves nothing");
            Assert.That(substanceThrown, Is.Null,
                "a substance PATCH must not enter the embedding-regeneration branch — "
                + "an exception here means TouchesName (or its caller) was widened to /substance");
            Assert.That(live.Substance, Is.EqualTo("S9|after"),
                "the substance write must have committed — an unchanged value means the PATCH was silently dropped rather than isolated");
        });
    }

    [Test, Parallelizable]
    [Description("C7 — invariant I5: when the embedding regeneration throws mid-transaction the node keeps its previous content and its substance, both rolled back together.")]
    public async Task UploadContent_WhenEmbeddingRegenerationFails_LeavesSubstanceIntact()
    {
        using DatabaseFixture fixture = new();
        NodeService seedSvc = new(fixture.EntityManager, DisabledCapability);
        NodeService uploadSvc = new(fixture.EntityManager, EnabledCapability);

        NodeDetails node = await seedSvc.CreateNode(
            new NodeDetails { Type = "documentation", Name = "C7_Subject" },
            callerId: 0);

        await seedSvc.UploadContent(
            node.Id,
            "text/markdown",
            new MemoryStream(Encoding.UTF8.GetBytes("# C7 v1 body that must survive the failed upload")),
            callerId: 0, isAdmin: true, CancellationToken.None);
        await seedSvc.Patch(
            node.Id,
            [new PatchOperation { Op = "replace", Path = "/substance", Value = "C7|must-survive-rollback" }],
            callerId: 0, isAdmin: true, CancellationToken.None);

        StatementException? thrown = null;
        try
        {
            await uploadSvc.UploadContent(
                node.Id,
                "text/markdown",
                new MemoryStream(Encoding.UTF8.GetBytes("# C7 v2 body that must not commit")),
                callerId: 0, isAdmin: true, CancellationToken.None);
        }
        catch (StatementException ex)
        {
            thrown = ex;
        }

        Node live = await fixture.EntityManager.Load<Node>()
                                               .Where(n => n.Id == node.Id)
                                               .ExecuteEntityAsync();
        string? liveContent = live.Content == null ? null : Encoding.UTF8.GetString(live.Content);

        Assert.Multiple(() => {
            Assert.That(thrown, Is.Not.Null,
                "scenario check: the upload must reach the Postgres-only embedding function and throw on SQLite - "
                + "null here means the transaction committed and the assertions below prove nothing");
            Assert.That(liveContent, Is.EqualTo("# C7 v1 body that must survive the failed upload"),
                "the pre-existing content must survive the failed upload - the v2 body here means the content write committed "
                + "despite the embedding failure, and null means the seeded content never landed");
            Assert.That(live.Substance, Is.EqualTo("C7|must-survive-rollback"),
                "the substance clear must roll back with the content write - a null here means the clear was issued outside the transaction");
        });
    }
}
