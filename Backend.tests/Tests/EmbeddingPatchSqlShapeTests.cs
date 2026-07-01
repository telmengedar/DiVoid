#nullable disable
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Moq;
using NUnit.Framework;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Info;

namespace Backend.tests.Tests;

/// <summary>
/// SQL-shape assertions for the four-branch embedding UPDATE (task #444, PR #81).
///
/// Jenny's review (DiVoid #685) found that the R-fixtures in
/// <see cref="EmbeddingPatchSqlCompositionTests"/> do not pin the SQL form — they assert
/// <c>EmbeddingInputComposer.Compose</c> output (invariant under SQL changes) or
/// "embedding IS NULL on SQLite" (always true, SQL form never executes there).
///
/// Round 2 (DiVoid #723 / PR #91): the original SS1–SS8 replicated the operation tree
/// in static helper methods (F1Sql/F3Sql/etc.), making the tests tautological — the
/// helpers moved in lock-step with any production change, so a bug reintroduction
/// still passed.  Jenny's substitution proof: reverting NodeService.cs to Option B
/// without touching the test file gave 8 pass / 0 fail.
///
/// Fix: tests now call <see cref="GoogleMlEmbeddingProvider.BuildEmbeddingBranchOperations"/> — the
/// <c>internal static</c> shared helper that builds the same UPDATE trees as the
/// production path — and render each operation’s SQL via <c>Prepare().CommandText</c>
/// in the test-local <c>RenderAllBranches</c> helper.  Service code carries no test-only
/// surface.  A reversion of NodeService.cs alone now causes SS1 and SS5 to fail.
///
/// Two negative-proof substitutions are pinned:
///
/// NP1 — drop the allowlist <c>= ANY()</c> clause from F1/F2/F3/F4.
///   Expected failure: "expected = ANY( clause in F1 WHERE but was absent".
///
/// NP2 — swap the truncation operator from <c>LEFT( convert_from( ... ) )</c> (Option A)
///   back to <c>convert_from( LEFT( ... ) )</c> (Option B) in F1/F3.
///   Expected failure: "F1 SET must NOT use convert_from( LEFT( ... ) ) nesting (Option B, rejected)".
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

    /// <summary>
    /// Calls <see cref="GoogleMlEmbeddingProvider.BuildEmbeddingBranchOperations"/> — the internal
    /// shared helper — once and renders each operation to SQL via <c>Prepare().CommandText</c>.
    /// This is the only place the operation tree is constructed; any change to
    /// <see cref="GoogleMlEmbeddingProvider.BuildEmbeddingBranchOperations"/> automatically propagates here.
    /// </summary>
    static (string F1, string F2, string F3, string F4) RenderAllBranches()
    {
        IEntityManager em = CreatePostgresEntityManager();
        (UpdateValuesOperation<Node> f1, UpdateValuesOperation<Node> f2,
         UpdateValuesOperation<Node> f3, UpdateValuesOperation<Node> f4) =
            GoogleMlEmbeddingProvider.BuildEmbeddingBranchOperations(em, nodeId: 1L, TextContentTypePredicate.EmbeddingModel);
        return (
            f1.Prepare().CommandText,
            f2.Prepare().CommandText,
            f3.Prepare().CommandText,
            f4.Prepare().CommandText
        );
    }

    // -----------------------------------------------------------------------
    // SS1 — F1 SET expression uses LEFT( convert_from( ... ) ) nesting (Option A, char-aware)
    //
    // NP2 substitution: in NodeService.cs only, swap
    //   DB.Left(DB.ConvertFrom(..., "UTF8"), 8000) back to
    //   DB.ConvertFrom(DB.Left(..., 8000), "UTF8") inside BuildEmbeddingBranchOperations / F1.
    // Expected failure: sql contains convert_from( LEFT( and the Does.Not.Contain fires
    //   with "Option B, rejected" — exactly the crash-on-multibyte-boundary error.
    // -----------------------------------------------------------------------

    [Test]
    public void SS1_F1_SetExpression_ContainsLeftOuterConvertFromInner()
    {
        string sql = RenderAllBranches().F1;

        Assert.Multiple(() => {
            Assert.That(sql, Does.Contain("LEFT( convert_from("),
                "SS1: F1 SET must use LEFT( convert_from( ... ) ) nesting (Option A, char-aware); " +
                "revert NodeService.cs to Option B (convert_from( LEFT( ... ) )) and this fails with " +
                "invalid UTF-8 byte sequence on multi-byte chars spanning the 8000-byte boundary");
            Assert.That(sql, Does.Not.Contain("convert_from( LEFT("),
                "SS1: F1 SET must NOT use convert_from( LEFT( ... ) ) nesting (Option B, rejected)");
        });
    }

    // -----------------------------------------------------------------------
    // SS2 — F1 WHERE contains the allowlist ANY clause (NP1 pin)
    //
    // NP1 substitution: in NodeService.cs only, remove n.ContentType.In(allowlist)
    //   from the F1 WHERE inside BuildEmbeddingBranchOperations, leaving only Like("text/%").
    // Expected failure: SQL no longer contains = ANY( and this assertion fires.
    // -----------------------------------------------------------------------

    [Test]
    public void SS2_F1_WhereClause_ContainsAllowlistAny()
    {
        string sql = RenderAllBranches().F1;

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
        string sql = RenderAllBranches().F1;

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
        string sql = RenderAllBranches().F2;

        Assert.That(sql, Does.Contain("= ANY("),
            "SS4: F2 WHERE must contain = ANY( for the allowlist negation; " +
            "without it application/* types are silently routed into F2 instead of F1");
    }

    // -----------------------------------------------------------------------
    // SS5 — F3 SET expression uses LEFT( convert_from( ... ) ) nesting (Option A, char-aware)
    //
    // NP2 substitution: in NodeService.cs only, swap DB.Left(DB.ConvertFrom(..., "UTF8"), 8000)
    //   back to DB.ConvertFrom(DB.Left(..., 8000), "UTF8") in the F3 SET.
    // Expected failure: SQL contains convert_from( LEFT( and the Does.Not.Contain fires.
    // -----------------------------------------------------------------------

    [Test]
    public void SS5_F3_SetExpression_ContainsLeftOuterConvertFromInner()
    {
        string sql = RenderAllBranches().F3;

        Assert.Multiple(() => {
            Assert.That(sql, Does.Contain("LEFT( convert_from("),
                "SS5: F3 SET must use LEFT( convert_from( ... ) ) nesting (Option A, char-aware); " +
                "revert NodeService.cs to Option B (convert_from( LEFT( ... ) )) and this fails (NP2)");
            Assert.That(sql, Does.Not.Contain("convert_from( LEFT("),
                "SS5: F3 SET must NOT use convert_from( LEFT( ... ) ) nesting (Option B, rejected)");
        });
    }

    // -----------------------------------------------------------------------
    // SS6 — F3 WHERE contains the allowlist ANY clause
    // -----------------------------------------------------------------------

    [Test]
    public void SS6_F3_WhereClause_ContainsAllowlistAny()
    {
        string sql = RenderAllBranches().F3;

        Assert.That(sql, Does.Contain("= ANY("),
            "SS6: F3 WHERE must contain = ANY( for the ApplicationTextTypes allowlist IN clause");
    }

    // -----------------------------------------------------------------------
    // SS7 — F4 WHERE contains the allowlist ANY clause
    // -----------------------------------------------------------------------

    [Test]
    public void SS7_F4_WhereClause_ContainsAllowlistAny()
    {
        string sql = RenderAllBranches().F4;

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
        string sql = RenderAllBranches().F4;

        Assert.Multiple(() => {
            Assert.That(sql, Does.Contain("= NULL"),
                "SS8: F4 SET must be SET embedding = NULL");
            Assert.That(sql, Does.Not.Contain("embedding ("),
                "SS8: F4 SET must NOT call the embedding() function — null branch sets NULL directly");
        });
    }
}
