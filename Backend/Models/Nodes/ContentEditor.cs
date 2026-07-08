using System.Linq;
using System.Text;
using Backend.Services.Embeddings;

namespace Backend.Models.Nodes;

/// <summary>
/// pure helper that applies an ordered set of range replacements to a node's text content.
///
/// all logic lives here (no I/O, no SQL, no DI) so the anti-corruption rules — text-only,
/// strict UTF-8, code-point boundaries, bounds checks, overlap rejection — are exercised by
/// fast unit tests without a database.  the service layer only supplies the bytes and persists
/// the result.
///
/// addressing model: every edit is resolved against the content <em>as read</em> (not against
/// the evolving result of earlier edits in the same batch), so a caller computes all offsets
/// from a single snapshot.  overlapping ranges are rejected rather than silently merged.
/// </summary>
internal static class ContentEditor
{
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// applies <paramref name="edits"/> to <paramref name="content"/> and returns the new content bytes.
    /// </summary>
    /// <param name="content">current content bytes (treated as empty when null)</param>
    /// <param name="contentType">MIME type of the content; must classify as text</param>
    /// <param name="edits">ordered range replacements, all addressed against the original content</param>
    /// <returns>the edited content re-encoded as UTF-8 (no BOM)</returns>
    /// <exception cref="ArgumentException">
    /// thrown for every caller-input fault — no edits, non-text content, invalid UTF-8, a range
    /// outside the content, or overlapping ranges.  mapped to HTTP 400 by
    /// <see cref="Backend.Errors.ArgumentExceptionHandler"/>.  the operation is all-or-nothing:
    /// on any fault no partial result is produced.
    /// </exception>
    internal static byte[] Apply(byte[] content, string contentType, ContentEdit[] edits)
    {
        if (edits == null || edits.Length == 0)
            throw new ArgumentException("no content edits supplied");

        if (!TextContentTypePredicate.IsText(contentType))
            throw new ArgumentException($"range editing requires text content; content type '{contentType}' is not text");

        string text = Decode(content);

        int[] codePointOffsets = BuildCodePointOffsets(text);
        int[] lineOffsets = BuildLineOffsets(text);

        (int Start, int End, string Value)[] resolved = new (int, int, string)[edits.Length];
        for (int i = 0; i < edits.Length; i++)
            resolved[i] = Resolve(edits[i], codePointOffsets, lineOffsets);

        int[] order = Enumerable.Range(0, resolved.Length)
                                .OrderBy(i => resolved[i].Start)
                                .ThenBy(i => i)
                                .ToArray();

        StringBuilder builder = new(text.Length);
        int cursor = 0;
        int previousEnd = 0;
        for (int k = 0; k < order.Length; k++)
        {
            (int start, int end, string value) = resolved[order[k]];
            if (k > 0 && start < previousEnd)
                throw new ArgumentException("content edits overlap");

            builder.Append(text, cursor, start - cursor);
            builder.Append(value ?? string.Empty);
            cursor = end;
            previousEnd = end;
        }
        builder.Append(text, cursor, text.Length - cursor);

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>
    /// decodes content bytes as strict UTF-8, treating a null array as empty content.
    /// </summary>
    static string Decode(byte[] content)
    {
        if (content == null || content.Length == 0)
            return string.Empty;
        try
        {
            return StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            throw new ArgumentException("content is not valid UTF-8; range editing is not supported");
        }
    }

    /// <summary>
    /// resolves one edit to a half-open UTF-16 index range against the original text.
    /// the returned boundaries always fall on code-point boundaries, so splicing them can
    /// never split a surrogate pair.
    /// </summary>
    static (int Start, int End, string Value) Resolve(ContentEdit edit, int[] codePointOffsets, int[] lineOffsets)
    {
        if (edit.Start < 0 || edit.Length < 0)
            throw new ArgumentException($"edit start ({edit.Start}) and length ({edit.Length}) must be non-negative");

        int[] offsets = edit.Unit == ContentEditUnit.Line ? lineOffsets : codePointOffsets;
        int count = offsets.Length - 1;
        long end = (long)edit.Start + edit.Length;
        if (end > count)
        {
            string unit = edit.Unit == ContentEditUnit.Line ? "line" : "character";
            throw new ArgumentException(
                $"{unit} range [{edit.Start}, {end}) is out of bounds; content has {count} {unit}s");
        }

        return (offsets[edit.Start], offsets[(int)end], edit.Value);
    }

    /// <summary>
    /// maps code-point index to UTF-16 offset.  the array has one entry per code point plus a
    /// trailing sentinel equal to the string length, so index N addresses the end of the text.
    /// </summary>
    static int[] BuildCodePointOffsets(string text)
    {
        List<int> offsets = [];
        int i = 0;
        while (i < text.Length)
        {
            offsets.Add(i);
            i += char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
        }
        offsets.Add(text.Length);
        return [.. offsets];
    }

    /// <summary>
    /// maps line index to the UTF-16 offset where that line begins.  lines split on <c>\n</c>;
    /// the trailing sentinel equals the string length.  content with no newline is one line;
    /// a trailing newline yields a final empty line.
    /// </summary>
    static int[] BuildLineOffsets(string text)
    {
        List<int> starts = [0];
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n')
                starts.Add(i + 1);
        starts.Add(text.Length);
        return [.. starts];
    }
}
