using Backend.Services.Embeddings;
using NUnit.Framework;

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
    public void Compose_NameAndTextContent_TruncatesAtPolicyMaxLength()
    {
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
