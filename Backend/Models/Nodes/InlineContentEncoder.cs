using System;
using System.Text;
using Backend.Services.Embeddings;

namespace Backend.Models.Nodes;

/// <summary>
/// pure helper that encodes a node's raw content bytes into the inline JSON representation
/// used by the <c>content</c> field on <see cref="NodeDetails"/> listing rows.
///
/// text content (per <see cref="TextContentTypePredicate.IsText"/>) is decoded as UTF-8 and
/// returned as a JSON string, with the decode boundary backed up to the last complete UTF-8
/// code-point when the cap splits a multi-byte character.  a byte sequence that claims a text
/// MIME type but contains invalid UTF-8 is silently demoted to the base64 path.
///
/// binary content (including unknown or null <c>contentType</c>) is base64-encoded.
///
/// truncation always operates on the raw byte stream; base64-encoding happens after the byte
/// prefix is extracted, so the base64 output size reflects exactly <c>maxBytes</c> source bytes
/// (not a 64 KiB slice of the already-encoded string).
///
/// an empty or null byte array returns <see cref="EncodeResult.Empty"/>.
/// </summary>
internal static class InlineContentEncoder
{
    /// <summary>
    /// maximum inline bytes per row: 64 KiB.
    /// rows whose raw content exceeds this are truncated; the original length is preserved in
    /// <see cref="EncodeResult.OriginalLength"/> and <see cref="EncodeResult.Truncated"/> is set.
    /// </summary>
    internal const int MaxInlineBytes = 64 * 1024;

    /// <summary>
    /// encodes raw content bytes for inline listing inclusion.
    /// </summary>
    /// <param name="content">raw content bytes from <c>Node.Content</c>; null or empty yields <see cref="EncodeResult.Empty"/>.</param>
    /// <param name="contentType">MIME type string from <c>Node.ContentType</c>; null is treated as binary.</param>
    /// <param name="maxBytes">per-row byte cap; defaults to <see cref="MaxInlineBytes"/> when &lt;= 0.</param>
    /// <returns>encode result carrying the encoded string and truncation metadata.</returns>
    internal static EncodeResult Encode(byte[] content, string contentType, int maxBytes = 0)
    {
        if (content == null || content.Length == 0)
            return EncodeResult.Empty;

        int cap = maxBytes > 0 ? maxBytes : MaxInlineBytes;
        bool truncated = content.Length > cap;
        long originalLength = content.Length;

        byte[] source = truncated ? content[..cap] : content;

        if (TextContentTypePredicate.IsText(contentType))
        {
            string decoded = TryDecodeUtf8(source, truncated, cap, content, out byte[] actualSource);
            if (decoded != null)
                return new EncodeResult(decoded, truncated && actualSource.Length < originalLength, originalLength);

            // silent demotion: claimed text but invalid UTF-8 — fall through to base64
        }

        return new EncodeResult(Convert.ToBase64String(source), truncated, originalLength);
    }

    /// <summary>
    /// attempts to decode <paramref name="source"/> as UTF-8.
    /// when the byte prefix ends in the middle of a multi-byte character the boundary is
    /// backed up to the last complete code-point before decoding.
    /// </summary>
    /// <param name="source">the byte slice to decode (already capped).</param>
    /// <param name="wasTruncated">whether the original content was longer than the cap.</param>
    /// <param name="cap">the original cap value used to slice <paramref name="source"/>.</param>
    /// <param name="original">full original byte array, used only when backing up the UTF-8 boundary.</param>
    /// <param name="actualSource">the source slice actually decoded (may be shorter after UTF-8 back-up).</param>
    /// <returns>decoded string, or null if UTF-8 decoding fails.</returns>
    static string TryDecodeUtf8(byte[] source, bool wasTruncated, int cap, byte[] original, out byte[] actualSource)
    {
        actualSource = source;
        if (!wasTruncated)
        {
            try
            {
                return Encoding.UTF8.GetString(source);
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }

        // back up to the last complete UTF-8 code-point boundary within the cap
        int boundary = FindUtf8Boundary(original, cap);
        if (boundary < source.Length)
            actualSource = original[..boundary];

        try
        {
            return Encoding.UTF8.GetString(actualSource);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// returns the largest byte index &lt;= <paramref name="limit"/> that falls on a complete
    /// UTF-8 code-point boundary, i.e. the last position where the next byte is either a
    /// single-byte codepoint (0x00–0x7F) or the start of a multi-byte sequence (0xC0–0xFF).
    /// returns 0 when no such boundary exists within <paramref name="limit"/> (degenerate input).
    /// </summary>
    static int FindUtf8Boundary(byte[] bytes, int limit)
    {
        int pos = Math.Min(limit, bytes.Length);
        while (pos > 0)
        {
            byte b = bytes[pos - 1];
            // single-byte or start of multi-byte sequence: we are at a clean boundary
            if (b < 0x80 || b >= 0xC0)
                return pos;
            // continuation byte (0x80–0xBF): step back
            --pos;
        }
        return 0;
    }
}
