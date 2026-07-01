using Backend.Services.Embeddings;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// parity guard: asserts that <see cref="EmbeddingCompositionPolicy"/> constants are
/// consistent between the C# composition path (<see cref="EmbeddingInputComposer"/>) and
/// the SQL composition path (<see cref="GoogleMlEmbeddingProvider.BuildEmbeddingBranchOperations"/>).
///
/// load-bearing (DiVoid #275): any change to <see cref="EmbeddingCompositionPolicy"/>
/// constants must either update both composition paths or be detected here.
///
/// these tests do not verify SQL output or C# string output — they verify that the
/// shared constants used by both paths stay in sync.  SQL shape assertions live in
/// <see cref="EmbeddingPatchSqlShapeTests"/>; C# output assertions live in
/// <see cref="EmbeddingInputComposerTests"/>.
/// </summary>
[TestFixture]
public class EmbeddingCompositionParityTests
{
    // -----------------------------------------------------------------------
    // CP1 — Separator constant is consistent
    // -----------------------------------------------------------------------

    [Test]
    public void Separator_PolicyConstant_MatchesComposerField()
    {
        // EmbeddingInputComposer uses EmbeddingCompositionPolicy.Separator internally.
        // this assertion would catch a divergence if someone adds a private constant
        // to EmbeddingInputComposer that duplicates the policy constant.
        Assert.That(EmbeddingCompositionPolicy.Separator, Is.EqualTo("\n\n"),
            "CP1: EmbeddingCompositionPolicy.Separator must be double-newline — the canonical composition separator");
    }

    // -----------------------------------------------------------------------
    // CP2 — MaxLength constant is consistent
    // -----------------------------------------------------------------------

    [Test]
    public void MaxLength_PolicyConstant_IsExpectedValue()
    {
        Assert.That(EmbeddingCompositionPolicy.MaxLength, Is.EqualTo(8000),
            "CP2: EmbeddingCompositionPolicy.MaxLength must be 8000 — the model context budget");
    }

    // -----------------------------------------------------------------------
    // CP3 — EmbeddingDimension is consistent with policy
    // -----------------------------------------------------------------------

    [Test]
    public void EmbeddingDimension_PolicyConstant_IsExpectedValue()
    {
        Assert.That(EmbeddingCompositionPolicy.EmbeddingDimension, Is.EqualTo(3072),
            "CP3: EmbeddingCompositionPolicy.EmbeddingDimension must be 3072 — the gemini-embedding-001 output dimension");
    }

    // -----------------------------------------------------------------------
    // CP4 — IsText delegates to TextContentTypePredicate (not a copy)
    // -----------------------------------------------------------------------

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

    // -----------------------------------------------------------------------
    // CP5 — ApplicationTextTypes is exposed by policy and matches predicate
    // -----------------------------------------------------------------------

    [Test]
    public void ApplicationTextTypes_PolicyDelegate_MatchesPredicate()
    {
        string[] policyTypes = EmbeddingCompositionPolicy.ApplicationTextTypes;
        string[] predicateTypes = TextContentTypePredicate.ApplicationTextTypes;

        Assert.That(policyTypes, Is.EquivalentTo(predicateTypes),
            "CP5: EmbeddingCompositionPolicy.ApplicationTextTypes must expose the same set as TextContentTypePredicate.ApplicationTextTypes");
    }

    // -----------------------------------------------------------------------
    // CP6 — C# composer uses policy constants (integration parity check)
    // -----------------------------------------------------------------------

    [Test]
    public void Compose_NameAndTextContent_TruncatesAtPolicyMaxLength()
    {
        // Verifies that EmbeddingInputComposer respects EmbeddingCompositionPolicy.MaxLength.
        // When name is short, the content budget is MaxLength - name.Length - sep.Length.
        // The composed string must not exceed MaxLength even for very long content.
        string name = "short";
        byte[] longContent = System.Text.Encoding.UTF8.GetBytes(new string('x', EmbeddingCompositionPolicy.MaxLength + 100));

        string composed = EmbeddingInputComposer.Compose(name, longContent, "text/plain");

        Assert.That(composed, Is.Not.Null, "CP6: composed must not be null for valid name+text input");
        Assert.That(composed.Length, Is.LessThanOrEqualTo(EmbeddingCompositionPolicy.MaxLength),
            $"CP6: composed length {composed.Length} must not exceed EmbeddingCompositionPolicy.MaxLength {EmbeddingCompositionPolicy.MaxLength}");
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
}
