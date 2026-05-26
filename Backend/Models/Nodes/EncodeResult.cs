namespace Backend.Models.Nodes;

/// <summary>
/// result of <see cref="InlineContentEncoder.Encode"/>.
/// carries the encoded string and truncation metadata for one node content row.
/// </summary>
internal readonly struct EncodeResult
{
    /// <summary>
    /// a sentinel that represents no content (null content bytes).
    /// all fields are null / default.
    /// </summary>
    internal static readonly EncodeResult Empty = default;

    /// <summary>
    /// creates a populated encode result.
    /// </summary>
    /// <param name="encoded">the encoded string (UTF-8 text or base64).</param>
    /// <param name="truncated">true when the source exceeded the byte cap.</param>
    /// <param name="originalLength">original source byte length.</param>
    internal EncodeResult(string encoded, bool truncated, long originalLength)
    {
        Encoded = encoded;
        Truncated = truncated;
        OriginalLength = originalLength;
    }

    /// <summary>
    /// the encoded content string; null when there was no content to encode.
    /// </summary>
    internal string Encoded { get; }

    /// <summary>
    /// true when the source content exceeded the per-row byte cap and was truncated.
    /// </summary>
    internal bool Truncated { get; }

    /// <summary>
    /// the original byte length of the full content before truncation.
    /// only meaningful when <see cref="Truncated"/> is true.
    /// </summary>
    internal long OriginalLength { get; }
}
