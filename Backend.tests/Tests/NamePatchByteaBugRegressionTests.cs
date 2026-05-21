#nullable disable
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Backend.Init;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Moq;
using NUnit.Framework;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Errors;
using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Expressions;
using Pooshit.Ocelot.Fields;
using Pooshit.Ocelot.Info;
using Pooshit.Ocelot.Tokens;

namespace Backend.tests.Tests;

/// <summary>
/// Regression tests for DiVoid #781: PATCH /api/nodes/{id} with /name path returning
/// HTTP 500 because <c>LEFT(bytea, integer)</c> does not exist in Postgres (error 42883).
///
/// Fix (PR fix/backend-embedding-regen-bytea-substring): replaced <c>DB.Left(...)</c>
/// with <c>DB.Substring(..., 1, 8000)</c> in F1 and F3 branches of
/// <c>NodeService.RegenerateEmbeddingViaBranches</c>. Ocelot's <c>SubstringToken</c>
/// emits <c>SUBSTRING(expr, start, length)</c> on Postgres, which accepts <c>bytea</c>
/// arguments; <c>LeftToken</c> emits <c>LEFT(expr, length)</c> which Postgres only
/// accepts for <c>text</c>, not <c>bytea</c>.
///
/// Two test layers:
///
/// Layer 1 — SQL-shape (no DB needed): builds the same operation tree as the production
/// helper using a <see cref="PostgreInfo"/>-backed mock entity manager and renders the
/// UPDATE SQL via <c>Prepare().CommandText</c>. Asserts <c>SUBSTRING(</c> appears
/// while <c>LEFT(</c> does not.
///
/// Layer 2 — Postgres-gated integration (requires POSTGRES_CONNECTION): seeds a node
/// with bytea content, invokes <c>RegenerateEmbeddingViaBranches</c> directly (exposed
/// as <c>internal</c> via <c>[InternalsVisibleTo("Backend.tests")]</c>) against a real
/// Postgres, and asserts the failure (if any) is NOT the bytea-LEFT 42883.
/// In a deployment with the <c>embedding()</c> function installed the call succeeds;
/// without it the call fails with a different 42883 (function not found for
/// <c>embedding</c>), which is acceptable — the key is the absence of
/// <c>left(bytea, integer)</c> in the error message.
/// </summary>
[TestFixture, Parallelizable]
public class NamePatchByteaBugRegressionTests
{
    static readonly IEmbeddingCapability DisabledCapability = new EmbeddingCapability(false);

    // -----------------------------------------------------------------------
    // Layer 1 — SQL-shape (no real DB): renders UPDATE SQL using the same
    // token tree as the production helper and pins SUBSTRING, not LEFT.
    // -----------------------------------------------------------------------

    static IEntityManager CreatePostgresEntityManager()
    {
        Mock<IDBClient> clientMock = new();
        clientMock.SetupGet(c => c.DBInfo).Returns(new PostgreInfo());
        return new EntityManager(clientMock.Object);
    }

    /// <summary>
    /// SS9 (regression guard for DiVoid #781): the F1 UPDATE rendered with the fixed
    /// production token tree must contain <c>SUBSTRING(</c> and must NOT contain <c>LEFT(</c>.
    ///
    /// NP-revert proof: change <c>DB.Substring(..., 1, 8000)</c> back to
    /// <c>DB.Left(..., 8000)</c> in NodeService.cs F1. This test then fails with:
    ///   "SS9: F1 UPDATE must NOT contain LEFT( — LEFT(bytea,integer) throws 42883 on Postgres (DiVoid #781)"
    /// </summary>
    [Test, Parallelizable]
    public void SS9_F1_SqlShape_UsesSubstringNotLeft()
    {
        IEntityManager em = CreatePostgresEntityManager();

        // calls production RenderEmbeddingBranchSql directly — load-bearing per DiVoid #275
        // NP-revert: change DB.Substring -> DB.Left in RenderEmbeddingBranchSql F1.
        // This test then fails: "SS9: F1 UPDATE must NOT contain LEFT("
        string sql = NodeService.RenderEmbeddingBranchSql(em, 1L).F1Sql;

        Assert.Multiple(() => {
            Assert.That(sql, Does.Contain("SUBSTRING ("),
                "SS9: F1 UPDATE must contain SUBSTRING ( for bytea-safe truncation; " +
                "revert DB.Substring -> DB.Left in RenderEmbeddingBranchSql F1 to reproduce DiVoid #781");
            Assert.That(sql, Does.Not.Contain("LEFT("),
                "SS9: F1 UPDATE must NOT contain LEFT( — LEFT(bytea, integer) does not exist in Postgres (DiVoid #781)");
        });
    }

    /// <summary>
    /// SS10 (regression guard for DiVoid #781): the F3 UPDATE (content-only branch)
    /// rendered with the fixed production token tree must contain <c>SUBSTRING(</c>
    /// and must NOT contain <c>LEFT(</c>.
    ///
    /// NP-revert proof: change <c>DB.Substring(..., 1, 8000)</c> back to
    /// <c>DB.Left(..., 8000)</c> in NodeService.cs F3. This test then fails with:
    ///   "SS10: F3 UPDATE must NOT contain LEFT( — LEFT(bytea,integer) throws 42883 on Postgres (DiVoid #781)"
    /// </summary>
    [Test, Parallelizable]
    public void SS10_F3_SqlShape_UsesSubstringNotLeft()
    {
        IEntityManager em = CreatePostgresEntityManager();

        // calls production RenderEmbeddingBranchSql directly — load-bearing per DiVoid #275
        // NP-revert: change DB.Substring -> DB.Left in RenderEmbeddingBranchSql F3.
        // This test then fails: "SS10: F3 UPDATE must NOT contain LEFT("
        string sql = NodeService.RenderEmbeddingBranchSql(em, 1L).F3Sql;

        Assert.Multiple(() => {
            Assert.That(sql, Does.Contain("SUBSTRING ("),
                "SS10: F3 UPDATE must contain SUBSTRING ( for bytea-safe truncation; " +
                "revert DB.Substring -> DB.Left in RenderEmbeddingBranchSql F3 to reproduce DiVoid #781");
            Assert.That(sql, Does.Not.Contain("LEFT("),
                "SS10: F3 UPDATE must NOT contain LEFT( — LEFT(bytea, integer) does not exist in Postgres (DiVoid #781)");
        });
    }

    static IEntityManager CreatePostgresManager(string connString)
    {
        IDBClient client = ClientFactory.Create(() => new Npgsql.NpgsqlConnection(connString), new PostgreInfo(), true);
        return new EntityManager(client);
    }

    static async Task ApplySchema(IEntityManager em)
    {
        DatabaseModelService svc = new(em);
        await svc.StartAsync(CancellationToken.None);
    }

    static void PurgeTestData(IEntityManager em)
    {
        em.Delete<NodeLink>().Execute();
        em.Delete<Node>().Execute();
        em.Delete<NodeType>().Execute();
    }

    static NodeService MakeService(IEntityManager em) => new(em, DisabledCapability);

    static async Task<NodeDetails> SeedNode(NodeService svc, string name, byte[] content, string contentType)
    {
        NodeDetails created = await svc.CreateNode(new NodeDetails { Type = "documentation", Name = name });
        await svc.UploadContent(created.Id, contentType, new MemoryStream(content));
        return created;
    }

    /// <summary>
    /// PG1 (Postgres-gated regression): seed a node with bytea text content and name,
    /// then call <c>NodeService.RegenerateEmbeddingViaBranches</c> directly (internal,
    /// exposed via <c>[InternalsVisibleTo("Backend.tests")]</c>). Asserts the call does
    /// NOT throw a <c>StatementException</c> containing <c>left(bytea, integer)</c>
    /// (the DiVoid #781 error message from Npgsql).
    ///
    /// Acceptable: <c>embedding()</c> not installed → different 42883 for "embedding" function.
    /// Not acceptable: error contains <c>left(bytea</c> → original bug still present.
    ///
    /// Skipped when <c>POSTGRES_CONNECTION</c> is not set.
    /// </summary>
    [Test, Parallelizable]
    public async Task PG1_RegenerateEmbeddingViaBranches_Postgres_DoesNotThrowByteaLeftError()
    {
        string connString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION");
        if (string.IsNullOrEmpty(connString))
            Assert.Inconclusive("POSTGRES_CONNECTION not set — Postgres bytea-fix regression test skipped (DiVoid #781)");

        IEntityManager em = CreatePostgresManager(connString);
        await ApplySchema(em);
        PurgeTestData(em);

        NodeService svc = MakeService(em);

        byte[] content = Encoding.UTF8.GetBytes("# test\n\nbytea content for DiVoid #781 regression");
        NodeDetails node = await SeedNode(svc, "divoid-781-regression-node", content, "text/markdown");

        try {
            using Transaction tx = em.Transaction();
            await NodeService.RegenerateEmbeddingViaBranches(em, tx, node.Id, CancellationToken.None);
            tx.Commit();
            // embedding() installed and fix correct: reaches here — test passes
        } catch (StatementException ex) {
            // acceptable: embedding() not installed → different 42883 for "embedding" function
            // NOT acceptable: "left(bytea, integer)" — that is the original bug (DiVoid #781)
            Assert.That(ex.Message, Does.Not.Contain("left(bytea"),
                $"PG1: RegenerateEmbeddingViaBranches must NOT throw 'left(bytea, integer) does not exist' " +
                $"(DiVoid #781 regression). Full exception: {ex}");
        } finally {
            PurgeTestData(em);
        }
    }
}
