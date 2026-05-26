using System;
using System.Text;
using Backend.Models.Nodes;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// unit tests for <see cref="InlineContentEncoder"/>.
/// pure function — no database, DI, or HTTP stack needed.
/// covers encoding branches, truncation rules, and edge cases in isolation
/// so failures are immediately diagnosable without the full HTTP path.
/// </summary>
[TestFixture]
public class InlineContentEncoderTests
{

    [Test, Parallelizable]
    public void Encode_NullContent_ReturnsEmpty()
    {
        EncodeResult result = InlineContentEncoder.Encode(null, "text/plain");

        Assert.That(result.Encoded, Is.Null, "null content must yield null Encoded");
        Assert.That(result.Truncated, Is.False);
    }

    [Test, Parallelizable]
    public void Encode_EmptyContent_ReturnsEmpty()
    {
        EncodeResult result = InlineContentEncoder.Encode([], "text/plain");

        Assert.That(result.Encoded, Is.Null, "empty content must yield null Encoded");
    }

    [Test, Parallelizable]
    public void Encode_TextContent_ReturnsUtf8String()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("hello world");
        EncodeResult result = InlineContentEncoder.Encode(bytes, "text/plain");

        Assert.That(result.Encoded, Is.EqualTo("hello world"));
        Assert.That(result.Truncated, Is.False);
    }

    [Test, Parallelizable]
    public void Encode_ApplicationJsonContent_ReturnsUtf8String()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("{\"a\":1}");
        EncodeResult result = InlineContentEncoder.Encode(bytes, "application/json");

        Assert.That(result.Encoded, Is.EqualTo("{\"a\":1}"), "application/json is text");
    }

    [Test, Parallelizable]
    public void Encode_BinaryContent_ReturnsBase64()
    {
        byte[] bytes = [0x01, 0x02, 0x03];
        EncodeResult result = InlineContentEncoder.Encode(bytes, "image/png");

        Assert.That(result.Encoded, Is.EqualTo(Convert.ToBase64String(bytes)));
        Assert.That(result.Truncated, Is.False);
    }

    [Test, Parallelizable]
    public void Encode_NullContentType_TreatedAsBinary()
    {
        byte[] bytes = [0xDE, 0xAD, 0xBE, 0xEF];
        EncodeResult result = InlineContentEncoder.Encode(bytes, null);

        Assert.That(result.Encoded, Is.EqualTo(Convert.ToBase64String(bytes)), "null contentType must default to base64");
    }

    [Test, Parallelizable]
    public void Encode_TextWithCharsetSuffix_ReturnsUtf8String()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("data");
        EncodeResult result = InlineContentEncoder.Encode(bytes, "text/plain; charset=utf-8");

        Assert.That(result.Encoded, Is.EqualTo("data"), "charset suffix must be stripped before text/binary decision");
    }

    [Test, Parallelizable]
    public void Encode_TextExactlyAtCap_NotTruncated()
    {
        int cap = InlineContentEncoder.MaxInlineBytes;
        byte[] bytes = Encoding.ASCII.GetBytes(new string('X', cap));
        EncodeResult result = InlineContentEncoder.Encode(bytes, "text/plain", cap);

        Assert.That(result.Truncated, Is.False, "content at exactly the cap must not be truncated");
        Assert.That(result.Encoded!.Length, Is.EqualTo(cap));
    }

    [Test, Parallelizable]
    public void Encode_TextOneByteOverCap_Truncated()
    {
        int cap = 100;
        byte[] bytes = Encoding.ASCII.GetBytes(new string('Z', cap + 1));
        EncodeResult result = InlineContentEncoder.Encode(bytes, "text/plain", cap);

        Assert.That(result.Truncated, Is.True);
        Assert.That(result.OriginalLength, Is.EqualTo(cap + 1));
        Assert.That(result.Encoded!.Length, Is.EqualTo(cap));
    }

    [Test, Parallelizable]
    public void Encode_BinaryOverCap_TruncatesBytesBeforeBase64()
    {
        int cap = 100;
        byte[] bytes = new byte[cap + 50];
        new Random(1).NextBytes(bytes);
        EncodeResult result = InlineContentEncoder.Encode(bytes, "application/octet-stream", cap);

        Assert.That(result.Truncated, Is.True);
        Assert.That(result.OriginalLength, Is.EqualTo(bytes.Length));
        byte[] decoded = Convert.FromBase64String(result.Encoded!);
        Assert.That(decoded.Length, Is.EqualTo(cap), "base64 must decode to exactly cap bytes of the original");
        Assert.That(decoded, Is.EqualTo(bytes[..cap]), "decoded bytes must be the leading cap bytes");
    }

    [Test, Parallelizable]
    [Description("guards the UTF-8 boundary back-up rule: the encoder must not split a multi-byte character at the cap")]
    public void Encode_MultibyteBoundary_BacksUpToCompleteCodepoint()
    {
        int cap = 10;
        // 9 ASCII bytes + one 3-byte UTF-8 char that straddles the cap
        byte[] prefix = Encoding.ASCII.GetBytes(new string('A', 9));
        byte[] cjk = Encoding.UTF8.GetBytes("中");
        byte[] bytes = [.. prefix, .. cjk];

        EncodeResult result = InlineContentEncoder.Encode(bytes, "text/plain", cap);

        Assert.That(result.Truncated, Is.True);
        Assert.That(() => Encoding.UTF8.GetBytes(result.Encoded!), Throws.Nothing,
            "encoded text must be valid UTF-8");
        Assert.That(Encoding.UTF8.GetByteCount(result.Encoded!), Is.LessThanOrEqualTo(cap));
        Assert.That(result.Encoded, Is.EqualTo("AAAAAAAAA"),
            "result must end at the last complete code-point before the cap");
    }

    [Test, Parallelizable]
    [Description("guards the silent-demotion rule: invalid UTF-8 in a text-typed node must fall back to base64")]
    public void Encode_InvalidUtf8InTextTyped_DemotesToBase64()
    {
        byte[] bytes = [0x41, 0x42, 0xFF, 0x43];
        EncodeResult result = InlineContentEncoder.Encode(bytes, "text/plain");

        Assert.That(result.Encoded, Is.Not.Null, "must return a result, not throw");
        byte[] decoded = Convert.FromBase64String(result.Encoded!);
        Assert.That(decoded, Is.EqualTo(bytes), "silently-demoted content must round-trip as base64");
    }
}
