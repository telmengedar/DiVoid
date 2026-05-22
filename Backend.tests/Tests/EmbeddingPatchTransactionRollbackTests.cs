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
/// regression pin for the transaction-rollback invariant on name-PATCH embedding regen
/// (task #445, architecture doc DiVoid #440 §10 / §13).
///
/// PR #68 (task #437) wraps two UPDATEs inside one transaction in
/// <c>NodeService.Patch</c>:
///   UPDATE 1 — apply JSON-Patch operations (name, status, etc.)
///   UPDATE 2 — regenerate the embedding column via four SQL-side branches
///
/// the invariant: if the embedding regen fails (UPDATE 2 throws), UPDATE 1 must also be
/// rolled back.  a future refactor that splits the transaction (e.g. pulls the embedding
/// regen to a background job, or forgets to pass the transaction object to ExecuteAsync)
/// would silently violate the invariant and is exactly what this test catches.
///
/// fault-injection mechanism:
/// SQLite does not implement the <c>DB.ConvertFrom</c> / <c>convert_from()</c>
/// Postgres function.  the test seeds a node using a disabled-capability service (so
/// CreateNode does not attempt the embedding call), then calls Patch with an
/// enabled-capability service on the same EntityManager.  with capability enabled,
/// NodeService.Patch enters the embedding branch, executes UPDATE 1 (name) inside the
/// open transaction, then calls RegenerateEmbeddingViaBranches which prepares all four
/// SQL branch UPDATEs including the F1 branch that uses <c>DB.ConvertFrom</c>.  Ocelot
/// throws <c>NotSupportedException</c> ("DB.ConvertFrom is only supported on PostgreSQL")
/// at SQL-preparation time, before <c>transaction.Commit()</c> is reached.  the
/// <c>using Transaction</c> scope disposes without committing → SQLite rolls back
/// UPDATE 1.
///
/// load-bearing (DiVoid #275) — substitution proof documented in XML below.
/// the broken production code is never committed.
///
/// DB engine: SQLite only (the embedded SQLite engine is the fault source; the
/// Postgres-specific DB.ConvertFrom token is what triggers the exception).
/// the rollback semantics (exception before Commit → Dispose rolls back) are identical
/// on Postgres — this is a standard ADO.NET transaction contract.  on Postgres the
/// analogous fault would be a real embedding-model RPC failure; here the SQLite dialect
/// rejection is structurally equivalent: both throw before Commit(), both exercise the
/// same <c>using Transaction</c> rollback path.
/// </summary>
[TestFixture]
public class EmbeddingPatchTransactionRollbackTests
{
    static readonly IEmbeddingCapability DisabledCapability = new EmbeddingCapability(false);
    static readonly IEmbeddingCapability EnabledCapability = new EmbeddingCapability(true);


    /// <summary>
    /// core regression: embedding-branch failure mid-transaction rolls back the name UPDATE.
    ///
    /// arrange: node seeded with name "Original" via a disabled-capability service so that
    ///          CreateNode does not attempt any embedding call.
    /// act:     PATCH /name → "New" via an enabled-capability service on the same EntityManager.
    ///          Ocelot throws NotSupportedException (DB.ConvertFrom not supported on SQLite)
    ///          inside the transaction scope, before Commit().
    /// assert:  exception propagates; live row still has name "Original" (UPDATE 1 rolled back).
    ///
    /// substitution proof (DiVoid #275):
    ///   in NodeService.Patch, move transaction.Commit() to BEFORE the nameTouched block
    ///   (i.e. commit UPDATE 1 before RegenerateEmbeddingViaBranches runs).  re-running
    ///   this test then shows live.Name == "New" — the commit landed before the embedding
    ///   threw, so the name change is permanent even though the embedding step failed.
    ///   the assertion "UPDATE 1 (name) must be rolled back" fails.  restoring the commit
    ///   to after the embedding block makes it pass.  the broken code is never committed.
    /// </summary>
    [Test]
    public async Task Patch_EmbeddingThrowsMidTransaction_NameUpdateRolledBack()
    {
        using DatabaseFixture fixture = new();
        NodeService seedSvc = new(fixture.EntityManager, DisabledCapability);
        NodeService patchSvc = new(fixture.EntityManager, EnabledCapability);

        NodeDetails node = await seedSvc.CreateNode(new NodeDetails { Type = "task", Name = "Original" });

        Exception? thrown = null;
        try
        {
            await patchSvc.Patch(
                node.Id,
                [new PatchOperation { Op = "replace", Path = "/name", Value = "New" }],
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            thrown = ex;
        }

        Node live = await fixture.EntityManager.Load<Node>()
                                               .Where(n => n.Id == node.Id)
                                               .ExecuteEntityAsync();

        Assert.Multiple(() => {
            Assert.That(thrown, Is.Not.Null,
                "embedding branch on SQLite must throw (DB.ConvertFrom is Postgres-only) — if null the capability guard is not entering the embedding branch");
            Assert.That(live.Name, Is.EqualTo("Original"),
                "UPDATE 1 (name) must be rolled back: transaction must not commit when the embedding step throws");
            Assert.That(live.Embedding, Is.Null,
                "no partial embedding write: exception before any embedding SET executes, so embedding must remain null");
        });
    }


    /// <summary>
    /// confirms the exception from the embedding branch mentions the Postgres-only restriction,
    /// so future Ocelot versions that add SQLite support for ConvertFrom would not silently
    /// pass the test for the wrong reason.
    /// </summary>
    [Test]
    public async Task Patch_EmbeddingThrowsMidTransaction_ExceptionMentionsPostgresRestriction()
    {
        using DatabaseFixture fixture = new();
        NodeService seedSvc = new(fixture.EntityManager, DisabledCapability);
        NodeService patchSvc = new(fixture.EntityManager, EnabledCapability);

        NodeDetails node = await seedSvc.CreateNode(new NodeDetails { Type = "documentation", Name = "OriginalDoc" });

        Exception? thrown = null;
        try
        {
            await patchSvc.Patch(
                node.Id,
                [new PatchOperation { Op = "replace", Path = "/name", Value = "NewDoc" }],
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            thrown = ex;
        }

        Assert.That(thrown, Is.Not.Null,
            "exception must be thrown from the embedding branch — capability enabled on SQLite triggers Postgres-only DB.ConvertFrom");
        Assert.That(thrown!.Message, Does.Contain("PostgreSQL").IgnoreCase,
            "exception message must reference PostgreSQL restriction — distinguishes embedding-branch throw from unrelated failures");
    }


    /// <summary>
    /// non-name PATCH does not enter the embedding branch regardless of the capability flag —
    /// confirming the fault in the rollback test is caused by the embedding path, not by some
    /// other transaction issue.
    /// </summary>
    [Test]
    public async Task Patch_NonNameField_EmbeddingCapabilityEnabled_NoThrow()
    {
        using DatabaseFixture fixture = new();
        NodeService seedSvc = new(fixture.EntityManager, DisabledCapability);
        NodeService patchSvc = new(fixture.EntityManager, EnabledCapability);

        NodeDetails node = await seedSvc.CreateNode(
            new NodeDetails { Type = "task", Name = "StableNode", Status = "open" });

        NodeDetails patched = await patchSvc.Patch(
            node.Id,
            [new PatchOperation { Op = "replace", Path = "/status", Value = "closed" }],
            CancellationToken.None);

        Assert.That(patched.Status, Is.EqualTo("closed"),
            "non-name PATCH must succeed even with capability enabled — the embedding branch is only entered when the name is touched");
    }
}
