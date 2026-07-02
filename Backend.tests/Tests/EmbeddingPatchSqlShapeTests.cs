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
/// NP1 — drop the allowlist <c>= ANY()</c> clause from any WHEN condition.
///   expected failure: "expected = ANY( but was absent".
///
/// NP2 — swap the truncation operator from <c>LEFT( convert_from( ... ) )</c> (Option A, char-aware)
///   back to <c>convert_from( LEFT( ... ) )</c> (Option B) in the name+content or content-only WHEN branch.
///   expected failure: <c>Does.Not.Contain("convert_from( LEFT(")</c> fires.
/// </summary>
[TestFixture]
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

    [Test]
    public void SS2_NameContentBranch_WhenConditionContainsAllowlistAny()
    {
        string sql = RenderSingleUpdate();

        Assert.That(sql, Does.Contain("= ANY("),
            "SS2: name+content WHEN condition must contain = ANY( for the ApplicationTextTypes allowlist; " +
            "removing it causes application/json etc. to silently drop into the name-only branch (NP1)");
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
                "SS3: name+content WHEN must include ILIKE for text/* wildcard");
        });
    }

    [Test]
    public void SS4_NameOnlyBranch_WhenConditionContainsAllowlistAny()
    {
        string sql = RenderSingleUpdate();

        Assert.That(sql, Does.Contain("= ANY("),
            "SS4: name-only WHEN condition must contain = ANY( for the allowlist negation; " +
            "without it application/* types are silently routed into this branch instead of the name+content branch (NP1)");
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

    [Test]
    public void SS6_ContentOnlyBranch_WhenConditionContainsAllowlistAny()
    {
        string sql = RenderSingleUpdate();

        Assert.That(sql, Does.Contain("= ANY("),
            "SS6: content-only WHEN condition must contain = ANY( for the ApplicationTextTypes allowlist");
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
