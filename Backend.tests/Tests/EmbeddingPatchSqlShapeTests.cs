#nullable disable
using System.Text.RegularExpressions;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Moq;
using NUnit.Framework;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Info;

namespace Backend.tests.Tests;

/// <summary>
/// SQL-shape assertions for the CASE-based embedding UPDATE (PR #154).
///
/// each test calls <see cref="GoogleMlEmbeddingProvider.BuildEmbeddingUpdate"/> — the internal
/// shared method that produces the same UPDATE tree as the production path — and renders it
/// to SQL via <c>Prepare().CommandText</c>.  a change to
/// <see cref="GoogleMlEmbeddingProvider.BuildEmbeddingUpdate"/> automatically propagates to all tests.
///
/// two negative-proof substitutions are pinned:
///
/// NP1 — restore the allowlist <c>= ANY()</c> clause in any WHEN condition (revert to allowlist form).
///   expected failure: "must NOT contain = ANY(" fires.
///   rationale: <c>= ANY(allowlist)</c> is an exact-match predicate that silently misclassifies
///   "application/json; charset=utf-8" — the CF#A bug.  the canonical gate is now an ILIKE
///   OR-chain built from <see cref="TextContentTypePredicate.TextPrefixes"/>.
///
/// NP2 — swap the truncation operator from <c>LEFT( convert_from( ... ) )</c> (Option A, char-aware)
///   back to <c>convert_from( LEFT( ... ) )</c> (Option B) in the name+content or content-only WHEN branch.
///   expected failure: <c>Does.Not.Contain("convert_from( LEFT(")</c> fires.
/// </summary>
[TestFixture, Parallelizable]
public class EmbeddingPatchSqlShapeTests
{
    static IEntityManager CreatePostgresEntityManager()
    {
        Mock<IDBClient> clientMock = new();
        clientMock.SetupGet(c => c.DBInfo).Returns(new PostgreInfo());
        return new EntityManager(clientMock.Object);
    }

    /// <summary>
    /// renders <see cref="GoogleMlEmbeddingProvider.BuildEmbeddingUpdate"/> to SQL.
    /// this is the single source of truth for all SS assertions.
    /// </summary>
    static string RenderSingleUpdate()
    {
        IEntityManager em = CreatePostgresEntityManager();
        UpdateValuesOperation<Node> op =
            GoogleMlEmbeddingProvider.BuildEmbeddingUpdate(em, nodeId: 1L, TextContentTypePredicate.EmbeddingModel);
        return op.Prepare().CommandText;
    }

    [Test]
    public void SS1_NameContentBranch_SetUsesLeftOuterConvertFromInner()
    {
        string sql = RenderSingleUpdate();

        Assert.Multiple(() => {
            Assert.That(sql, Does.Contain("LEFT( convert_from("),
                "SS1: name+content WHEN branch SET must use LEFT( convert_from( ... ) ) nesting (Option A, char-aware); " +
                "revert to convert_from( LEFT( ... ) ) (Option B) and this fails with " +
                "invalid UTF-8 byte sequence on multi-byte chars at the 8000-byte boundary");
            Assert.That(sql, Does.Not.Contain("convert_from( LEFT("),
                "SS1: must NOT use convert_from( LEFT( ... ) ) nesting (Option B, rejected)");
        });
    }

    /// <summary>
    /// SS2 (NP1): content-type gate uses prefix ILIKE OR-chain, NOT the old = ANY(allowlist) exact-match.
    ///
    /// mental-deletion:
    ///   reverting to <c>ContentType.In(allowlist)</c> (= ANY) causes <c>Does.Not.Contain("= ANY(")</c>
    ///   to fire.  dropping a prefix from the OR-chain reduces the ILIKE count below
    ///   <c>TextPrefixes.Length × 3</c> and causes the count assertion to fail.
    /// </summary>
    [Test]
    public void SS2_ContentTypeGate_UsesPrefixIlikeChain_NotAllowlistIn()
    {
        string sql = RenderSingleUpdate();
        int ilikeCnt = Regex.Matches(sql, @"\bILIKE\b", RegexOptions.IgnoreCase).Count;
        int expectedIlike = TextContentTypePredicate.TextPrefixes.Length * 3;

        Assert.Multiple(() => {
            Assert.That(sql, Does.Not.Contain("= ANY("),
                "SS2 (NP1): content-type gate must NOT use = ANY(allowlist); " +
                "= ANY( is an exact-match predicate that misclassifies charset-suffixed types (CF#A); " +
                "the canonical gate is an ILIKE OR-chain from TextContentTypePredicate.TextPrefixes");
            Assert.That(ilikeCnt, Is.EqualTo(expectedIlike),
                $"SS2: exactly {expectedIlike} ILIKE patterns expected " +
                $"({TextContentTypePredicate.TextPrefixes.Length} prefixes × 3 WHEN branches); " +
                "dropping a prefix from the OR-chain reduces the count and mis-routes content types");
        });
    }

    [Test]
    public void SS3_NameContentBranch_WhenConditionContainsAllRequiredGuards()
    {
        string sql = RenderSingleUpdate();

        Assert.Multiple(() => {
            Assert.That(sql, Does.Contain("\"name\" IS NOT NULL"),
                "SS3: name+content WHEN must guard name IS NOT NULL");
            Assert.That(sql, Does.Contain("\"content\" IS NOT NULL"),
                "SS3: name+content WHEN must guard content IS NOT NULL");
            Assert.That(sql, Does.Contain("ILIKE"),
                "SS3: name+content WHEN must include ILIKE for prefix-based content-type matching");
        });
    }

    /// <summary>
    /// SS4: the name-only WHEN condition must not use the old = ANY(allowlist) negation.
    /// the content-type check in the name-only branch is the negation of the same prefix
    /// ILIKE OR-chain, not a negated exact-match IN predicate.
    /// </summary>
    [Test]
    public void SS4_NameOnlyBranch_ContentTypeNegation_NoPrefixAllowlistIn()
    {
        string sql = RenderSingleUpdate();

        Assert.That(sql, Does.Not.Contain("= ANY("),
            "SS4 (NP1): the name-only WHEN condition must not use = ANY(allowlist) for the content-type negation; " +
            "= ANY( indicates the old allowlist exact-match was restored (CF#A regression)");
    }

    [Test]
    public void SS5_ContentOnlyBranch_SetUsesLeftOuterConvertFromInner()
    {
        string sql = RenderSingleUpdate();

        Assert.Multiple(() => {
            Assert.That(sql, Does.Contain("LEFT( convert_from("),
                "SS5: content-only WHEN branch SET must use LEFT( convert_from( ... ) ) nesting (Option A, char-aware); " +
                "revert to Option B and this fails (NP2)");
            Assert.That(sql, Does.Not.Contain("convert_from( LEFT("),
                "SS5: must NOT use convert_from( LEFT( ... ) ) nesting (Option B, rejected)");
        });
    }

    /// <summary>
    /// SS6: the content-only WHEN condition must not use = ANY(allowlist).
    /// the content-type predicate is the same prefix ILIKE OR-chain used in WHEN 1/2.
    /// </summary>
    [Test]
    public void SS6_ContentOnlyBranch_WhenConditionUsesIlikeNotIn()
    {
        string sql = RenderSingleUpdate();

        Assert.That(sql, Does.Not.Contain("= ANY("),
            "SS6 (NP1): content-only WHEN condition must not use = ANY(allowlist); " +
            "the canonical gate is an ILIKE OR-chain from TextContentTypePredicate.TextPrefixes — " +
            "= ANY( indicates the old exact-match was restored (CF#A regression)");
    }

    [Test]
    public void SS7_CollapsedUpdate_ContainsElseNull()
    {
        string sql = RenderSingleUpdate();

        Assert.That(sql, Does.Contain("ELSE NULL"),
            "SS7: CASE must contain ELSE NULL for the no-embeddable-surface case; " +
            "nodes with neither name nor text content must have their embedding cleared");
    }

    [Test]
    public void SS8_CollapsedUpdate_HasSingleWhereClause()
    {
        string sql = RenderSingleUpdate();

        int count = Regex.Matches(sql, @"\bWHERE\b", RegexOptions.IgnoreCase).Count;
        Assert.That(count, Is.EqualTo(1),
            "SS8: the CASE-collapsed UPDATE must have exactly one WHERE clause — " +
            "four separate UPDATEs would each have their own WHERE and give count = 4");
    }

    [Test]
    public void SS9_CollapsedUpdate_IsCaseWhenExpression()
    {
        string sql = RenderSingleUpdate();

        Assert.That(sql, Does.Contain("CASE"),
            "SS9: SET expression must use a CASE expression to select the embedding branch server-side; " +
            "if this fails the CASE collapse was reverted back to four separate UPDATEs");
    }
}
