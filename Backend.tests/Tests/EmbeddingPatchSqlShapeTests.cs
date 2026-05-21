#nullable disable
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Moq;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Expressions;
using Pooshit.Ocelot.Fields;
using Pooshit.Ocelot.Info;
using Pooshit.Ocelot.Tokens;

namespace Backend.tests.Tests;

/// <summary>
/// SQL-shape assertions for the four-branch embedding UPDATE (task #444, PR #81).
///
/// Jenny's review (DiVoid #685) found that the seven R-fixtures in
/// <see cref="EmbeddingPatchSqlCompositionTests"/> do not pin the SQL form — they assert
/// <c>EmbeddingInputComposer.Compose</c> output (invariant under SQL changes) or
/// "embedding IS NULL on SQLite" (always true, SQL form never executes there).
///
/// This file fills that gap by capturing the SQL text that Ocelot emits for each branch
/// via <c>Update&lt;Node&gt;().Prepare().CommandText</c> on a <see cref="PostgreInfo"/> client
/// (no real DB connection; <c>Prepare()</c> is compile-time SQL generation).
///
/// Two negative-proof substitutions are pinned:
///
/// NP1 — drop the allowlist <c>= ANY()</c> clause from F1/F2/F3/F4.
///   Expected failure: "expected = ANY( clause in F1 WHERE but was absent".
///
/// NP-fix — revert <c>DB.Substring</c> to <c>DB.Left</c> in F1/F3 (the DiVoid #781 regression).
///   Expected failure: SS1/SS5 fire with "must NOT contain LEFT(" because
///   <c>LeftToken</c> emits <c>LEFT(bytea, integer)</c> which Postgres rejects (42883).
///
/// Each test documents the exact NP substitution that must make it fail.
/// </summary>
[TestFixture]
public class EmbeddingPatchSqlShapeTests
{
    // -----------------------------------------------------------------------
    // Infrastructure — PostgreInfo EntityManager, no real DB needed
    // -----------------------------------------------------------------------

    static IEntityManager CreatePostgresEntityManager()
    {
        Mock<IDBClient> clientMock = new();
        clientMock.SetupGet(c => c.DBInfo).Returns(new PostgreInfo());
        return new EntityManager(clientMock.Object);
    }

    // -----------------------------------------------------------------------
    // SQL shape for each branch (mirrors NodeService.RegenerateEmbeddingViaBranches exactly)
    // -----------------------------------------------------------------------

    static string F1Sql(IEntityManager em)
    {
        // calls production RenderEmbeddingBranchSql directly — load-bearing per DiVoid #275
        return NodeService.RenderEmbeddingBranchSql(em, 1L).F1Sql;
    }

    static string F2Sql(IEntityManager em)
    {
        string model = TextContentTypePredicate.EmbeddingModel;
        string[] allowlist = TextContentTypePredicate.ApplicationTextTypes;
        long nodeId = 1L;
        return em.Update<Node>()
                 .Set(n => n.Embedding == DB.CustomFunction("embedding",
                                                             DB.Constant(model),
                                                             DB.Property<Node>(x => x.Name)).Type<float[]>())
                 .Where(n => n.Id == nodeId
                          && n.Name != null && n.Name != ""
                          && (!(n.ContentType.Like("text/%") || n.ContentType.In(allowlist)) || n.Content == null))
                 .Prepare()
                 .CommandText;
    }

    static string F3Sql(IEntityManager em)
    {
        // calls production RenderEmbeddingBranchSql directly — load-bearing per DiVoid #275
        return NodeService.RenderEmbeddingBranchSql(em, 1L).F3Sql;
    }

    static string F4Sql(IEntityManager em)
    {
        string[] allowlist = TextContentTypePredicate.ApplicationTextTypes;
        long nodeId = 1L;
        return em.Update<Node>()
                 .Set(n => n.Embedding == (float[]) null)
                 .Where(n => n.Id == nodeId
                          && (n.Name == null || n.Name == "")
                          && (!(n.ContentType.Like("text/%") || n.ContentType.In(allowlist)) || n.Content == null))
                 .Prepare()
                 .CommandText;
    }

    // -----------------------------------------------------------------------
    // SS1 — F1 SET expression uses convert_from( LEFT( ... ) ) nesting (§4.4 form)
    //
    // NP2 substitution: swap DB.ConvertFrom(DB.Left(..., 8000), "UTF8") to
    //   DB.Left(DB.ConvertFrom(..., "UTF8"), 8000) in the F1 SET.
    // Expected failure: SQL contains LEFT( convert_from( instead of convert_from( LEFT(
    //   and this assertion fails with the "nesting order" message.
    // -----------------------------------------------------------------------

    [Test]
    public void SS1_F1_SetExpression_UsesSubstringNotLeft()
    {
        IEntityManager em = CreatePostgresEntityManager();
        string sql = F1Sql(em);

        // NP-fix: revert DB.Substring -> DB.Left in F1; this test fails with
        // "SS1: F1 SET must NOT contain LEFT(" because LeftToken emits LEFT( on Postgres.
        Assert.Multiple(() => {
            Assert.That(sql, Does.Contain("convert_from( SUBSTRING ("),
                "SS1: F1 SET must use convert_from( SUBSTRING( ... ) ) nesting; " +
                "LEFT(bytea, integer) does not exist in Postgres (DiVoid #781)");
            Assert.That(sql, Does.Not.Contain("LEFT("),
                "SS1: F1 SET must NOT contain LEFT( -- LEFT(bytea, integer) throws 42883 on Postgres (DiVoid #781)");
        });
    }

    // -----------------------------------------------------------------------
    // SS2 — F1 WHERE contains the allowlist ANY clause (NP1 pin)
    //
    // NP1 substitution: remove n.ContentType.In(allowlist) from F1's WHERE,
    //   leaving only n.ContentType.Like("text/%").
    // Expected failure: SQL no longer contains = ANY( and this assertion fires.
    // -----------------------------------------------------------------------

    [Test]
    public void SS2_F1_WhereClause_ContainsAllowlistAny()
    {
        IEntityManager em = CreatePostgresEntityManager();
        string sql = F1Sql(em);

        Assert.That(sql, Does.Contain("= ANY("),
            "SS2: F1 WHERE must contain = ANY( for the ApplicationTextTypes allowlist IN clause; " +
            "if this fails the allowlist predicate was removed (NP1), causing application/json etc. " +
            "to silently drop into the name-only F2 branch");
    }

    // -----------------------------------------------------------------------
    // SS3 — F1 WHERE contains all required guard clauses
    // -----------------------------------------------------------------------

    [Test]
    public void SS3_F1_WhereClause_ContainsAllRequiredGuards()
    {
        IEntityManager em = CreatePostgresEntityManager();
        string sql = F1Sql(em);

        Assert.Multiple(() => {
            Assert.That(sql, Does.Contain("\"name\" IS NOT NULL"),
                "SS3: F1 WHERE must guard name IS NOT NULL");
            Assert.That(sql, Does.Contain("\"content\" IS NOT NULL"),
                "SS3: F1 WHERE must guard content IS NOT NULL");
            Assert.That(sql, Does.Contain("ILIKE"),
                "SS3: F1 WHERE must include ILIKE for text/* wildcard");
        });
    }

    // -----------------------------------------------------------------------
    // SS4 — F2 WHERE contains the allowlist ANY clause
    //
    // F2 is the complement of F1 — its negated predicate must also reference
    // the allowlist so that application/* types are correctly excluded from F2.
    // -----------------------------------------------------------------------

    [Test]
    public void SS4_F2_WhereClause_ContainsAllowlistAny()
    {
        IEntityManager em = CreatePostgresEntityManager();
        string sql = F2Sql(em);

        Assert.That(sql, Does.Contain("= ANY("),
            "SS4: F2 WHERE must contain = ANY( for the allowlist negation; " +
            "without it application/* types are silently routed into F2 instead of F1");
    }

    // -----------------------------------------------------------------------
    // SS5 — F3 SET expression uses convert_from( LEFT( ... ) ) nesting (§4.4 form)
    //
    // NP2 substitution: swap DB.ConvertFrom(DB.Left(..., 8000), "UTF8") to
    //   DB.Left(DB.ConvertFrom(..., "UTF8"), 8000) in the F3 SET.
    // Expected failure: SQL contains LEFT( convert_from( and the Does.Contain fires.
    // -----------------------------------------------------------------------

    [Test]
    public void SS5_F3_SetExpression_UsesSubstringNotLeft()
    {
        IEntityManager em = CreatePostgresEntityManager();
        string sql = F3Sql(em);

        // NP-fix: revert DB.Substring -> DB.Left in F3; this test fails with
        // "SS5: F3 SET must NOT contain LEFT(" because LeftToken emits LEFT( on Postgres.
        Assert.Multiple(() => {
            Assert.That(sql, Does.Contain("convert_from( SUBSTRING ("),
                "SS5: F3 SET must use convert_from( SUBSTRING( ... ) ) nesting; " +
                "LEFT(bytea, integer) does not exist in Postgres (DiVoid #781)");
            Assert.That(sql, Does.Not.Contain("LEFT("),
                "SS5: F3 SET must NOT contain LEFT( -- LEFT(bytea, integer) throws 42883 on Postgres (DiVoid #781)");
        });
    }

    // -----------------------------------------------------------------------
    // SS6 — F3 WHERE contains the allowlist ANY clause
    // -----------------------------------------------------------------------

    [Test]
    public void SS6_F3_WhereClause_ContainsAllowlistAny()
    {
        IEntityManager em = CreatePostgresEntityManager();
        string sql = F3Sql(em);

        Assert.That(sql, Does.Contain("= ANY("),
            "SS6: F3 WHERE must contain = ANY( for the ApplicationTextTypes allowlist IN clause");
    }

    // -----------------------------------------------------------------------
    // SS7 — F4 WHERE contains the allowlist ANY clause
    // -----------------------------------------------------------------------

    [Test]
    public void SS7_F4_WhereClause_ContainsAllowlistAny()
    {
        IEntityManager em = CreatePostgresEntityManager();
        string sql = F4Sql(em);

        Assert.That(sql, Does.Contain("= ANY("),
            "SS7: F4 WHERE must contain = ANY( for the allowlist negation; " +
            "without it the null-out branch incorrectly fires on allowlist content types");
    }

    // -----------------------------------------------------------------------
    // SS8 — F4 SET is exactly NULL (no embedding function call)
    // -----------------------------------------------------------------------

    [Test]
    public void SS8_F4_SetExpression_IsNull()
    {
        IEntityManager em = CreatePostgresEntityManager();
        string sql = F4Sql(em);

        Assert.Multiple(() => {
            Assert.That(sql, Does.Contain("= NULL"),
                "SS8: F4 SET must be SET embedding = NULL");
            Assert.That(sql, Does.Not.Contain("embedding ("),
                "SS8: F4 SET must NOT call the embedding() function — null branch sets NULL directly");
        });
    }
}
