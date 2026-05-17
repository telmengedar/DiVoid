using System.Text;
using Backend.Services.Embeddings;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// unit tests for <see cref="EmbeddingInputComposer"/>.
/// pure function — no database or DI needed.
/// covers the full decision matrix from §11 Decision 3 of the embeddings-v2 architecture doc.
/// </summary>
[TestFixture]
public class EmbeddingInputComposerTests
{
    // -----------------------------------------------------------------------
    // 1. Decision-matrix cases (§11 Decision 3)
    // -----------------------------------------------------------------------

    [Test]
    public void Compose_NameAndTextContent_ReturnsNamePlusSeparatorPlusContent()
    {
        // name non-empty + text content non-empty → "name\n\ncontent"
        byte[] content = Encoding.UTF8.GetBytes("some body text");
        string result = EmbeddingInputComposer.Compose("My Node", content, "text/plain");

        Assert.That(result, Is.EqualTo("My Node\n\nsome body text"));
    }

    [Test]
    public void Compose_NameAndNonTextContent_ReturnsNameOnly()
    {
        // name non-empty + non-text content → "name"
        byte[] content = [0x89, 0x50, 0x4E, 0x47]; // PNG magic bytes
        string result = EmbeddingInputComposer.Compose("ImageNode", content, "image/png");

        Assert.That(result, Is.EqualTo("ImageNode"));
    }

    [Test]
    public void Compose_NameAndNullContent_ReturnsNameOnly()
    {
        // name non-empty + null content → "name"
        string result = EmbeddingInputComposer.Compose("NameOnly", null, null);

        Assert.That(result, Is.EqualTo("NameOnly"));
    }

    [Test]
    public void Compose_NameAndEmptyContent_ReturnsNameOnly()
    {
        // name non-empty + empty byte array → "name"
        string result = EmbeddingInputComposer.Compose("NameOnly", [], "text/plain");

        Assert.That(result, Is.EqualTo("NameOnly"));
    }

    [Test]
    public void Compose_EmptyNameAndTextContent_ReturnsContentOnly()
    {
        // name empty + text content non-empty → content alone
        byte[] content = Encoding.UTF8.GetBytes("body without a name");
        string result = EmbeddingInputComposer.Compose("", content, "text/markdown");

        Assert.That(result, Is.EqualTo("body without a name"));
    }

    [Test]
    public void Compose_WhitespaceNameAndTextContent_ReturnsContentOnly()
    {
        // whitespace-only name is treated as empty
        byte[] content = Encoding.UTF8.GetBytes("body text");
        string result = EmbeddingInputComposer.Compose("   ", content, "text/plain");

        Assert.That(result, Is.EqualTo("body text"));
    }

    [Test]
    public void Compose_EmptyNameAndNonTextContent_ReturnsNull()
    {
        // name empty + non-text content → null (no embeddable surface)
        byte[] content = [0xFF, 0xD8, 0xFF]; // JPEG magic
        string result = EmbeddingInputComposer.Compose("", content, "image/jpeg");

        Assert.That(result, Is.Null);
    }

    [Test]
    public void Compose_NullNameAndNullContent_ReturnsNull()
    {
        // both null → null
        string result = EmbeddingInputComposer.Compose(null, null, null);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void Compose_EmptyNameAndNullContent_ReturnsNull()
    {
        string result = EmbeddingInputComposer.Compose("", null, null);

        Assert.That(result, Is.Null);
    }

    // -----------------------------------------------------------------------
    // 2. Application/* allowlist passes through as text
    // -----------------------------------------------------------------------

    [Test]
    public void Compose_ApplicationJson_TreatedAsText()
    {
        byte[] content = Encoding.UTF8.GetBytes("{\"key\":\"value\"}");
        string result = EmbeddingInputComposer.Compose("JsonNode", content, "application/json");

        Assert.That(result, Is.EqualTo("JsonNode\n\n{\"key\":\"value\"}"));
    }

    // -----------------------------------------------------------------------
    // 3. Length cap behaviour
    // -----------------------------------------------------------------------

    [Test]
    public void Compose_LongContent_ContentTruncatedToFitBudget()
    {
        // content longer than MaxLength after name + separator
        string name = "Short";
        int separatorLen = 2; // "\n\n"
        int budget = EmbeddingInputComposer.MaxLength - name.Length - separatorLen;
        string longBody = new string('x', budget + 100); // exceeds budget

        byte[] content = Encoding.UTF8.GetBytes(longBody);
        string result = EmbeddingInputComposer.Compose(name, content, "text/plain");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Length, Is.LessThanOrEqualTo(EmbeddingInputComposer.MaxLength),
            "composed output must not exceed MaxLength");
        Assert.That(result, Does.StartWith("Short\n\n"),
            "name + separator must be preserved at the start");
    }

    [Test]
    public void Compose_ContentExactlyAtBudget_NotTruncated()
    {
        string name = "N";
        int separatorLen = 2;
        int budget = EmbeddingInputComposer.MaxLength - name.Length - separatorLen;
        string body = new string('y', budget);

        byte[] content = Encoding.UTF8.GetBytes(body);
        string result = EmbeddingInputComposer.Compose(name, content, "text/plain");

        Assert.That(result.Length, Is.EqualTo(EmbeddingInputComposer.MaxLength));
    }

    [Test]
    public void Compose_LongNameOnly_TruncatedToMaxLength()
    {
        // name longer than MaxLength (pathological input) — name-only output is truncated
        string longName = new string('a', EmbeddingInputComposer.MaxLength + 50);
        string result = EmbeddingInputComposer.Compose(longName, null, null);

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Length, Is.EqualTo(EmbeddingInputComposer.MaxLength));
    }

    [Test]
    public void Compose_LongTextContentNoName_TruncatedToMaxLength()
    {
        // name empty + text content longer than MaxLength → truncated content only
        string longBody = new string('z', EmbeddingInputComposer.MaxLength + 200);
        byte[] content = Encoding.UTF8.GetBytes(longBody);
        string result = EmbeddingInputComposer.Compose("", content, "text/plain");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Length, Is.EqualTo(EmbeddingInputComposer.MaxLength));
    }

    // -----------------------------------------------------------------------
    // 4. Edge cases: charset suffixes, non-UTF-8 bytes
    // -----------------------------------------------------------------------

    [Test]
    public void Compose_TextWithCharsetSuffix_TreatedAsText()
    {
        // "text/plain; charset=utf-8" must be classified as text
        byte[] content = Encoding.UTF8.GetBytes("hello");
        string result = EmbeddingInputComposer.Compose("Node", content, "text/plain; charset=utf-8");

        Assert.That(result, Is.EqualTo("Node\n\nhello"));
    }

    [Test]
    public void Compose_NonUtf8Bytes_DoesNotThrow()
    {
        // non-UTF-8 bytes decode with replacement characters — result is non-null and non-empty
        byte[] invalidUtf8 = [0x80, 0x81, 0x82]; // continuation bytes without a leading byte
        string result = EmbeddingInputComposer.Compose("MyNode", invalidUtf8, "text/plain");

        Assert.That(result, Is.Not.Null,
            "non-UTF-8 bytes are decoded with replacement — mojibake is acceptable per architecture doc");
    }
}
