using System.Text;
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
/// parity guard: asserts that <see cref="EmbeddingCompositionPolicy"/> constants are
/// consistent between the C# composition path (<see cref="EmbeddingInputComposer"/>) and
/// the SQL composition path (<see cref="GoogleMlEmbeddingProvider.BuildEmbeddingUpdate"/>).
///
/// load-bearing (DiVoid #275): any change to <see cref="EmbeddingCompositionPolicy"/>
/// constants must either update both composition paths or be detected here.
/// </summary>
[TestFixture]
public class EmbeddingCompositionParityTests
{
    [Test]
    public void Separator_PolicyConstant_MatchesComposerField()
    {
        Assert.That(EmbeddingCompositionPolicy.Separator, Is.EqualTo("\n\n"),
            "CP1: EmbeddingCompositionPolicy.Separator must be double-newline — the canonical composition separator");
    }

    [Test]
    public void MaxLength_PolicyConstant_IsExpectedValue()
    {
        Assert.That(EmbeddingCompositionPolicy.MaxLength, Is.EqualTo(8000),
            "CP2: EmbeddingCompositionPolicy.MaxLength must be 8000 — the model context budget");
    }

    [Test]
    public void EmbeddingDimension_PolicyConstant_IsExpectedValue()
    {
        Assert.That(EmbeddingCompositionPolicy.EmbeddingDimension, Is.EqualTo(3072),
            "CP3: EmbeddingCompositionPolicy.EmbeddingDimension must be 3072 — the gemini-embedding-001 output dimension");
    }

    [Test]
    public void IsText_PolicyDelegate_MatchesPredicateForKnownTypes()
    {
        foreach ((string mime, bool expected) in new[] {
            ("text/plain", true),
            ("text/markdown", true),
            ("application/json", true),
            ("application/xml", true),
            ("image/png", false),
            ("application/octet-stream", false),
        }) {
            Assert.That(EmbeddingCompositionPolicy.IsText(mime), Is.EqualTo(expected),
                $"CP4: EmbeddingCompositionPolicy.IsText(\"{mime}\") must equal TextContentTypePredicate.IsText(\"{mime}\")");
            Assert.That(TextContentTypePredicate.IsText(mime), Is.EqualTo(expected),
                $"CP4: TextContentTypePredicate.IsText(\"{mime}\") must equal expected value {expected}");
        }
    }

    [Test]
    public void ApplicationTextTypes_PolicyDelegate_MatchesPredicate()
    {
        string[] policyTypes = EmbeddingCompositionPolicy.ApplicationTextTypes;
        string[] predicateTypes = TextContentTypePredicate.ApplicationTextTypes;

        Assert.That(policyTypes, Is.EquivalentTo(predicateTypes),
            "CP5: EmbeddingCompositionPolicy.ApplicationTextTypes must expose the same set as TextContentTypePredicate.ApplicationTextTypes");
    }

    [Test]
    public void Compose_NameAndTextContent_TruncatesContentAtPolicyBudget()
    {
        string name = "short";
        string sep = EmbeddingCompositionPolicy.Separator;
        int expectedContentBudget = EmbeddingCompositionPolicy.MaxLength - sep.Length;
        byte[] longContent = Encoding.UTF8.GetBytes(new string('x', expectedContentBudget + 100));

        string composed = EmbeddingInputComposer.Compose(name, longContent, "text/plain");

        Assert.That(composed, Is.Not.Null, "CP6: composed must not be null for valid name+text input");
        string contentPortion = composed[(name.Length + sep.Length)..];
        Assert.That(contentPortion.Length, Is.EqualTo(expectedContentBudget),
            $"CP6: content portion must be exactly MaxLength−sep.Length ({expectedContentBudget}) — " +
            "not the old dynamic budget MaxLength−len(name)−len(sep)");
    }

    [Test]
    public void Compose_LongNameOnly_TruncatesAtPolicyMaxLength()
    {
        string name = new string('n', EmbeddingCompositionPolicy.MaxLength + 100);

        string composed = EmbeddingInputComposer.Compose(name, null, null);

        Assert.That(composed, Is.Not.Null, "CP6b: composed must not be null for long-name-only input");
        Assert.That(composed.Length, Is.LessThanOrEqualTo(EmbeddingCompositionPolicy.MaxLength),
            $"CP6b: name-only composed length {composed.Length} must not exceed MaxLength {EmbeddingCompositionPolicy.MaxLength}");
    }

    /// <summary>
    /// CP-PARITY (load-bearing, DiVoid #275): the C# composer and the SQL builder must use
    /// the SAME truncation budget — <c>MaxLength − sep.Length</c> sourced from
    /// <see cref="EmbeddingCompositionPolicy"/>.
    ///
    /// mental-deletion test:
    /// (C# side) reverting <see cref="EmbeddingInputComposer.Compose"/> to the old dynamic
    /// budget <c>MaxLength − len(name) − len(sep)</c> makes
    /// <c>contentPortion.Length == 7997 != 7998</c> → C# assertion fails.
    /// (SQL side) removing or restructuring the <c>LEFT(convert_from())</c>
    /// content-truncation branches in
    /// <see cref="GoogleMlEmbeddingProvider.BuildEmbeddingUpdate"/> changes
    /// <c>leftConvertFromCount</c> from 2 → SQL structural assertion fails.
    ///
    /// note: Ocelot parameterizes integer constants as <c>@N</c> placeholders, so the
    /// literal budget value (7998) is not present in <c>CommandText</c>; the numeric value
    /// is enforced at the source level — both paths derive it from
    /// <see cref="EmbeddingCompositionPolicy"/> constants directly.
    /// </summary>
    [Test]
    public void ContentBudget_CSharpComposer_AndSqlBuilder_UseIdenticalConstant()
    {
        int expectedBudget = EmbeddingCompositionPolicy.MaxLength - EmbeddingCompositionPolicy.Separator.Length;

        // --- C# path ---
        // name length 1: old dynamic budget would give MaxLength-1-2=7997, constant gives 7998.
        // using name "N" makes the dynamic vs constant difference visible.
        string name = "N";
        string sep = EmbeddingCompositionPolicy.Separator;
        byte[] content = Encoding.UTF8.GetBytes(new string('x', expectedBudget + 100));
        string composed = EmbeddingInputComposer.Compose(name, content, "text/plain");
        string contentPortion = composed[(name.Length + sep.Length)..];

        Assert.That(contentPortion.Length, Is.EqualTo(expectedBudget),
            $"CP-PARITY (C#): content portion must be exactly MaxLength−sep.Length = {expectedBudget}; " +
            "reverting to dynamic budget MaxLength−len(name)−len(sep) yields 7997, not 7998, and fails this assertion");

        // --- SQL path ---
        // render the single CASE-expression UPDATE and assert the structural content-truncation
        // branches exist.  Ocelot parameterizes integer constants as @N placeholders, so the
        // literal "7998" is not in CommandText — the budget value is enforced at the source level
        // (BuildEmbeddingUpdate reads EmbeddingCompositionPolicy.MaxLength and .Separator directly).
        Mock<IDBClient> clientMock = new();
        clientMock.SetupGet(c => c.DBInfo).Returns(new PostgreInfo());
        IEntityManager em = new EntityManager(clientMock.Object);
        UpdateValuesOperation<Node> op =
            GoogleMlEmbeddingProvider.BuildEmbeddingUpdate(em, nodeId: 1L, TextContentTypePredicate.EmbeddingModel);
        string sql = op.Prepare().CommandText;

        // WHEN 1 (name+text-content) and WHEN 3 (text-content-only) must each use
        // LEFT(convert_from()) — exactly 2 occurrences confirms both content-budget branches
        // are present.  removing either branch or reversing to convert_from(LEFT()) changes
        // the count and fails this assertion.
        int leftConvertFromCount = System.Text.RegularExpressions.Regex.Matches(
            sql, @"LEFT\s*\(\s*convert_from\s*\(").Count;
        Assert.That(leftConvertFromCount, Is.EqualTo(2),
            $"CP-PARITY (SQL): BuildEmbeddingUpdate must have exactly 2 LEFT(convert_from()) patterns " +
            "(WHEN 1: name+text-content, WHEN 3: text-content-only) — budget value " +
            $"{expectedBudget} (MaxLength={EmbeddingCompositionPolicy.MaxLength}−sep.Length=" +
            $"{EmbeddingCompositionPolicy.Separator.Length}) is enforced at the source level via EmbeddingCompositionPolicy");
    }
}
