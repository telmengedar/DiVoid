using System.Text;
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
/// parity guard: asserts that <see cref="EmbeddingCompositionPolicy"/> constants are
/// consistent between the C# composition path (<see cref="EmbeddingInputComposer"/>) and
/// the SQL composition path (<see cref="GoogleMlEmbeddingProvider.BuildEmbeddingUpdate"/>).
///
/// load-bearing (DiVoid #275): any change to <see cref="EmbeddingCompositionPolicy"/>
/// constants must either update both composition paths or be detected here.
///
/// CF#A resolution (DiVoid #2596/2597): the content-type gate is now a case-insensitive
/// prefix match over <see cref="TextContentTypePredicate.TextPrefixes"/> expressed identically
/// in C# (<see cref="TextContentTypePredicate.IsText"/>) and SQL (ILIKE OR-chain in
/// <see cref="GoogleMlEmbeddingProvider.BuildEmbeddingUpdate"/>).  tests CP7–CP10 guard
/// the four previously-divergent inputs.
/// </summary>
[TestFixture, Parallelizable]
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
    public void TextPrefixes_PolicyDelegate_MatchesPredicate()
    {
        string[] policyPrefixes = EmbeddingCompositionPolicy.TextPrefixes;
        string[] predicatePrefixes = TextContentTypePredicate.TextPrefixes;

        Assert.That(policyPrefixes, Is.EquivalentTo(predicatePrefixes),
            "CP5: EmbeddingCompositionPolicy.TextPrefixes must expose the same set as TextContentTypePredicate.TextPrefixes");
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
    /// CP7 (CF#A resolution): prefix match accepts charset suffixes without stripping.
    /// both C# (<see cref="TextContentTypePredicate.IsText"/>) and SQL (ILIKE 'application/json%')
    /// classify "application/json; charset=utf-8" as text.
    ///
    /// mental-deletion: reverting <see cref="TextContentTypePredicate.IsText"/> to exact-match
    /// (stripping before comparing) would return false here when the charset suffix is present
    /// and the bare type is not in a switch/allowlist, exposing CF#A.
    /// </summary>
    [Test]
    public void IsText_CharsetSuffix_BothPathsClassifyAsText()
    {
        Assert.Multiple(() => {
            Assert.That(TextContentTypePredicate.IsText("application/json; charset=utf-8"), Is.True,
                "CP7: prefix match must accept application/json with charset suffix — " +
                "reverting to exact-match returns false for this input (CF#A regression)");
            Assert.That(EmbeddingCompositionPolicy.IsText("application/json; charset=utf-8"), Is.True,
                "CP7: EmbeddingCompositionPolicy.IsText must agree — single source of truth via TextContentTypePredicate");
        });
    }

    /// <summary>
    /// CP8 (CF#A): case-insensitive prefix matching — uppercase and mixed-case MIME types accepted.
    ///
    /// mental-deletion: reverting <see cref="TextContentTypePredicate.IsText"/> to a case-sensitive
    /// comparison makes this return false for "TEXT/PLAIN" and "Application/JSON".
    /// </summary>
    [Test]
    public void IsText_UppercaseMimeTypes_ClassifiedAsText()
    {
        Assert.Multiple(() => {
            Assert.That(TextContentTypePredicate.IsText("TEXT/PLAIN"), Is.True,
                "CP8: uppercase TEXT/PLAIN must be recognised by case-insensitive prefix match");
            Assert.That(TextContentTypePredicate.IsText("Application/JSON"), Is.True,
                "CP8: mixed-case Application/JSON must be recognised by case-insensitive prefix match");
        });
    }

    /// <summary>
    /// CP9 (CF#A): SQL gate uses an ILIKE OR-chain derived from
    /// <see cref="TextContentTypePredicate.TextPrefixes"/>, not an exact-match <c>= ANY(allowlist)</c>.
    ///
    /// mental-deletion:
    ///   (a) reverting <see cref="GoogleMlEmbeddingProvider.BuildEmbeddingUpdate"/> to
    ///       <c>ContentType.In(allowlist)</c> introduces <c>= ANY(</c> in SQL → fails
    ///       <c>Does.Not.Contain</c>.
    ///   (b) dropping any prefix from the OR-chain reduces the ILIKE count below
    ///       <c>TextPrefixes.Length × 3</c> → fails the count assertion.
    /// </summary>
    [Test]
    public void SqlPredicate_UsesPrefixLikeChain_NotAllowlistIn()
    {
        Mock<IDBClient> clientMock = new();
        clientMock.SetupGet(c => c.DBInfo).Returns(new PostgreInfo());
        IEntityManager em = new EntityManager(clientMock.Object);
        UpdateValuesOperation<Node> op =
            GoogleMlEmbeddingProvider.BuildEmbeddingUpdate(em, nodeId: 1L, TextContentTypePredicate.EmbeddingModel);
        string sql = op.Prepare().CommandText;

        int ilikeCnt = Regex.Matches(sql, @"\bILIKE\b", RegexOptions.IgnoreCase).Count;
        int expectedIlike = TextContentTypePredicate.TextPrefixes.Length * 3;

        Assert.Multiple(() => {
            Assert.That(sql, Does.Not.Contain("= ANY("),
                "CP9(SQL): content-type gate must NOT use = ANY(allowlist) — " +
                "exact-match silently misclassifies 'application/json; charset=utf-8' (CF#A); " +
                "reverting to In(allowlist) causes this assertion to fail");
            Assert.That(ilikeCnt, Is.EqualTo(expectedIlike),
                $"CP9(SQL): exactly {expectedIlike} ILIKE patterns expected " +
                $"({TextContentTypePredicate.TextPrefixes.Length} prefixes × 3 WHEN conditions); " +
                "dropping a prefix from the OR-chain reduces the count and mis-routes content types");
        });
    }

    /// <summary>
    /// CP10 (CF#A): name-gate parity — C# uses <see cref="string.IsNullOrEmpty"/> (not
    /// IsNullOrWhiteSpace) so both paths treat whitespace-only names as non-empty,
    /// matching SQL where <c>Name != ''</c> passes whitespace.
    ///
    /// mental-deletion: reverting <see cref="EmbeddingInputComposer.Compose"/> to
    /// <c>IsNullOrWhiteSpace</c> makes <c>hasName=false</c> for "   " while SQL fires the
    /// name+content WHEN — the two paths diverge on whitespace-only names.
    /// </summary>
    [Test]
    public void NameGate_WhitespaceOnlyName_BothPathsTreatAsNonEmpty()
    {
        const string whitespaceOnly = "   ";

        bool csharpHasName = !string.IsNullOrEmpty(whitespaceOnly);
        Assert.That(csharpHasName, Is.True,
            "CP10(C#): !IsNullOrEmpty(\"   \") must be true — C# gate is IsNullOrEmpty (aligned to SQL Name != ''); " +
            "reverting to IsNullOrWhiteSpace gives false here, diverging from the SQL path (CF#A name divergence)");

        byte[] content = Encoding.UTF8.GetBytes("body");
        string composed = EmbeddingInputComposer.Compose(whitespaceOnly, content, "text/plain");
        Assert.Multiple(() => {
            Assert.That(composed, Is.Not.Null,
                "CP10(C#): Compose must return non-null for whitespace name + text content — " +
                "aligns with SQL w1 branch (Name != '' AND text content → name+content composition)");
            Assert.That(composed, Does.StartWith(whitespaceOnly),
                "CP10(C#): whitespace name preserved at start of composed output — " +
                "same branch as SQL WHEN 1 (name+content); reverting to IsNullOrWhiteSpace routes to content-only");
        });
    }

    /// <summary>
    /// CP-PARITY (load-bearing, DiVoid #275): the C# composer and the SQL builder must use
    /// the SAME truncation budget — <c>MaxLength − sep.Length</c> sourced from
    /// <see cref="EmbeddingCompositionPolicy"/>.
    ///
    /// mental-deletion:
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

        string name = "N";
        string sep = EmbeddingCompositionPolicy.Separator;
        byte[] content = Encoding.UTF8.GetBytes(new string('x', expectedBudget + 100));
        string composed = EmbeddingInputComposer.Compose(name, content, "text/plain");
        string contentPortion = composed[(name.Length + sep.Length)..];

        Assert.That(contentPortion.Length, Is.EqualTo(expectedBudget),
            $"CP-PARITY (C#): content portion must be exactly MaxLength−sep.Length = {expectedBudget}; " +
            "reverting to dynamic budget MaxLength−len(name)−len(sep) yields 7997, not 7998, and fails this assertion");

        Mock<IDBClient> clientMock = new();
        clientMock.SetupGet(c => c.DBInfo).Returns(new PostgreInfo());
        IEntityManager em = new EntityManager(clientMock.Object);
        UpdateValuesOperation<Node> op =
            GoogleMlEmbeddingProvider.BuildEmbeddingUpdate(em, nodeId: 1L, TextContentTypePredicate.EmbeddingModel);
        string sql = op.Prepare().CommandText;

        int leftConvertFromCount = Regex.Matches(
            sql, @"LEFT\s*\(\s*convert_from\s*\(").Count;
        Assert.That(leftConvertFromCount, Is.EqualTo(2),
            $"CP-PARITY (SQL): BuildEmbeddingUpdate must have exactly 2 LEFT(convert_from()) patterns " +
            "(WHEN 1: name+text-content, WHEN 3: text-content-only) — budget value " +
            $"{expectedBudget} (MaxLength={EmbeddingCompositionPolicy.MaxLength}−sep.Length=" +
            $"{EmbeddingCompositionPolicy.Separator.Length}) is enforced at the source level via EmbeddingCompositionPolicy");
    }
}
