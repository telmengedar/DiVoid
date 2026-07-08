using System.Text;
using Backend.Models.Nodes;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// unit tests for <see cref="ContentEditor"/> — the pure range-editing engine.
///
/// covers the primitive (replace / insert / delete / append), multi-edit original-frame
/// addressing, and every anti-corruption guard: non-text rejection, invalid UTF-8 rejection,
/// out-of-bounds rejection, overlap rejection, and code-point-boundary safety for multi-byte text.
/// </summary>
[TestFixture]
public class ContentEditorTests
{
    const string Text = "text/plain";

    static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
    static string Str(byte[] b) => Encoding.UTF8.GetString(b);

    static ContentEdit Char(int start, int length, string? value)
        => new() { Unit = ContentEditUnit.Char, Start = start, Length = length, Value = value! };

    static ContentEdit Line(int start, int length, string? value)
        => new() { Unit = ContentEditUnit.Line, Start = start, Length = length, Value = value! };

    [Test]
    public void Char_Replace_ReplacesRange()
    {
        byte[] result = ContentEditor.Apply(Bytes("hello world"), Text, [Char(0, 5, "hi")]);
        Assert.That(Str(result), Is.EqualTo("hi world"));
    }

    [Test]
    public void Line_Replace_ReplacesWholeLinesIncludingTerminators()
    {
        byte[] result = ContentEditor.Apply(Bytes("a\nb\nc\nd"), Text, [Line(1, 2, "X\n")]);
        Assert.That(Str(result), Is.EqualTo("a\nX\nd"));
    }

    [Test]
    public void Char_InsertZeroLength_InsertsWithoutDeleting()
    {
        byte[] result = ContentEditor.Apply(Bytes("abc"), Text, [Char(1, 0, "XY")]);
        Assert.That(Str(result), Is.EqualTo("aXYbc"));
    }

    [Test]
    public void Char_AppendAtEnd_AppendsContent()
    {
        byte[] result = ContentEditor.Apply(Bytes("abc"), Text, [Char(3, 0, "def")]);
        Assert.That(Str(result), Is.EqualTo("abcdef"));
    }

    [Test]
    public void Line_AppendAtEnd_AppendsLine()
    {
        byte[] result = ContentEditor.Apply(Bytes("a\nb"), Text, [Line(2, 0, "\nc")]);
        Assert.That(Str(result), Is.EqualTo("a\nb\nc"));
    }

    [Test]
    public void Char_DeleteWithEmptyValue_RemovesRange()
    {
        byte[] result = ContentEditor.Apply(Bytes("abcdef"), Text, [Char(2, 2, "")]);
        Assert.That(Str(result), Is.EqualTo("abef"));
    }

    [Test]
    public void Char_DeleteWithNullValue_RemovesRange()
    {
        byte[] result = ContentEditor.Apply(Bytes("abcdef"), Text, [Char(2, 2, null)]);
        Assert.That(Str(result), Is.EqualTo("abef"));
    }

    [Test]
    public void MultiEdit_OffsetsAddressedAgainstOriginalContent()
    {
        byte[] result = ContentEditor.Apply(Bytes("0123456789"), Text,
            [Char(0, 2, "AA"), Char(5, 2, "BB")]);
        Assert.That(Str(result), Is.EqualTo("AA234BB789"));
    }

    [Test]
    public void MultiEdit_OutOfOrderInput_ProducesSameResult()
    {
        byte[] result = ContentEditor.Apply(Bytes("0123456789"), Text,
            [Char(5, 2, "BB"), Char(0, 2, "AA")]);
        Assert.That(Str(result), Is.EqualTo("AA234BB789"));
    }

    [Test]
    public void MultiEdit_AdjacentRangesAllowed()
    {
        byte[] result = ContentEditor.Apply(Bytes("abcdef"), Text,
            [Char(0, 3, "X"), Char(3, 3, "Y")]);
        Assert.That(Str(result), Is.EqualTo("XY"));
    }

    [Test]
    public void EmptyEdits_Throws()
    {
        Assert.That(() => ContentEditor.Apply(Bytes("abc"), Text, []),
            Throws.ArgumentException);
    }

    [Test]
    public void NonTextContentType_Throws()
    {
        Assert.That(() => ContentEditor.Apply(Bytes("abc"), "application/octet-stream", [Char(0, 1, "x")]),
            Throws.ArgumentException);
    }

    [Test]
    [Description("0xFF is never a valid UTF-8 lead byte; strict decoding must reject it")]
    public void InvalidUtf8_Throws()
    {
        Assert.That(() => ContentEditor.Apply([0xFF, 0xFE], Text, [Char(0, 1, "x")]),
            Throws.ArgumentException);
    }

    [Test]
    public void CharRange_OutOfBounds_Throws()
    {
        Assert.That(() => ContentEditor.Apply(Bytes("abc"), Text, [Char(2, 5, "x")]),
            Throws.ArgumentException);
    }

    [Test]
    public void LineRange_OutOfBounds_Throws()
    {
        Assert.That(() => ContentEditor.Apply(Bytes("a\nb"), Text, [Line(5, 1, "x")]),
            Throws.ArgumentException);
    }

    [Test]
    public void NegativeStart_Throws()
    {
        Assert.That(() => ContentEditor.Apply(Bytes("abc"), Text, [Char(-1, 1, "x")]),
            Throws.ArgumentException);
    }

    [Test]
    public void OverlappingRanges_Throws()
    {
        Assert.That(() => ContentEditor.Apply(Bytes("abcdefgh"), Text, [Char(0, 5, "x"), Char(3, 3, "y")]),
            Throws.ArgumentException);
    }

    [Test]
    [Description("astral char (U+1F600) is one code point / two UTF-16 units; a range boundary must never split it")]
    public void AstralCharacter_CountsAsOneCharacter_NotSplit()
    {
        byte[] result = ContentEditor.Apply(Bytes("a😀b"), Text, [Char(1, 1, "X")]);
        Assert.That(Str(result), Is.EqualTo("aXb"));
    }

    [Test]
    public void AstralCharacter_InsertOnBoundary_KeepsCharacterIntact()
    {
        byte[] result = ContentEditor.Apply(Bytes("a😀b"), Text, [Char(1, 0, "Z")]);
        Assert.That(Str(result), Is.EqualTo("aZ😀b"));
    }

    [Test]
    [Description("é is two UTF-8 bytes but one code point at index 1; char addressing is code-point based")]
    public void MultiByteCharacter_AddressedByCodePoint()
    {
        byte[] result = ContentEditor.Apply(Bytes("héllo"), Text, [Char(1, 1, "e")]);
        Assert.That(Str(result), Is.EqualTo("hello"));
    }

    [Test]
    [Description("line splitting is on \\n only; a bare \\r stays attached to its line so CRLF files are undisturbed")]
    public void Crlf_LineEdit_LeavesCarriageReturnsUndisturbed()
    {
        byte[] result = ContentEditor.Apply(Bytes("a\r\nb\r\nc"), Text, [Line(1, 1, "Y\r\n")]);
        Assert.That(Str(result), Is.EqualTo("a\r\nY\r\nc"));
    }
}
