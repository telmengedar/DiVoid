using System;
using System.Text;
using Backend.Models.Nodes;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// unit tests for <see cref="InlineContentEncoder"/>.
/// pure function — no database, DI, or HTTP stack needed.
/// covers encoding branches and edge cases in isolation
/// so failures are immediately diagnosable without the full HTTP path.
/// </summary>
[TestFixture]
public class InlineContentEncoderTests
{

    [Test, Parallelizable]
    public void Encode_NullContent_ReturnsNull()
    {
        string result = InlineContentEncoder.Encode(null, "text/plain");

        Assert.That(result, Is.Null, "null content must yield null");
    }

    [Test, Parallelizable]
    public void Encode_EmptyContent_ReturnsNull()
    {
        string result = InlineContentEncoder.Encode([], "text/plain");

        Assert.That(result, Is.Null, "empty content must yield null");
    }

    [Test, Parallelizable]
    public void Encode_TextContent_ReturnsUtf8String()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("hello world");
        string result = InlineContentEncoder.Encode(bytes, "text/plain");

        Assert.That(result, Is.EqualTo("hello world"));
    }

    [Test, Parallelizable]
    public void Encode_ApplicationJsonContent_ReturnsUtf8String()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("{\"a\":1}");
        string result = InlineContentEncoder.Encode(bytes, "application/json");

        Assert.That(result, Is.EqualTo("{\"a\":1}"), "application/json is text");
    }

    [Test, Parallelizable]
    public void Encode_BinaryContent_ReturnsBase64()
    {
        byte[] bytes = [0x01, 0x02, 0x03];
        string result = InlineContentEncoder.Encode(bytes, "image/png");

        Assert.That(result, Is.EqualTo(Convert.ToBase64String(bytes)));
    }

    [Test, Parallelizable]
    public void Encode_NullContentType_TreatedAsBinary()
    {
        byte[] bytes = [0xDE, 0xAD, 0xBE, 0xEF];
        string result = InlineContentEncoder.Encode(bytes, null);

        Assert.That(result, Is.EqualTo(Convert.ToBase64String(bytes)), "null contentType must default to base64");
    }

    [Test, Parallelizable]
    public void Encode_TextWithCharsetSuffix_ReturnsUtf8String()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("data");
        string result = InlineContentEncoder.Encode(bytes, "text/plain; charset=utf-8");

        Assert.That(result, Is.EqualTo("data"), "charset suffix must be stripped before text/binary decision");
    }

    [Test, Parallelizable]
    public void Encode_TextMarkdown_ReturnsUtf8String()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("# heading");
        string result = InlineContentEncoder.Encode(bytes, "text/markdown");

        Assert.That(result, Is.EqualTo("# heading"), "text/markdown must be returned as UTF-8 string");
    }

    [Test, Parallelizable]
    public void Encode_ImagePng_ReturnsBase64()
    {
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47];
        string result = InlineContentEncoder.Encode(bytes, "image/png");

        Assert.That(result, Is.EqualTo(Convert.ToBase64String(bytes)), "image/png must be base64");
    }

    [Test, Parallelizable]
    public void Encode_UnknownContentType_ReturnsBase64()
    {
        byte[] bytes = [0x01, 0x02, 0x03];
        string result = InlineContentEncoder.Encode(bytes, "application/octet-stream");

        Assert.That(result, Is.EqualTo(Convert.ToBase64String(bytes)), "unknown/binary type must be base64");
    }
}
