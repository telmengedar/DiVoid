using System.Text;

namespace Backend.Services.Embeddings;

/// <summary>
/// single source of truth for the embedding composition policy.
/// given a node's name, content bytes, and content type, returns the texts
/// to feed to the embedding model: one text when the input fits, overlapping windows otherwise.
/// pure static helper: no I/O, no SQL, no DI dependencies.
/// </summary>
public static class EmbeddingInputComposer {

    /// <summary>
    /// maximum number of characters in the composed output string.
    /// the content portion is truncated to fit within this budget while the
    /// name + separator prefix is always preserved.
    /// heuristic for ~2k-token Gemini embedding budget (≈4 chars/token on average
    /// English+German prose); treat as a tunable constant, not a contract.
    /// </summary>
    public const int MaxLength = 8000;

    /// <summary>
    /// number of content characters in one window of a node whose composed input exceeds <see cref="MaxLength"/>
    /// </summary>
    public const int WindowLength = 7000;

    /// <summary>
    /// distance in content characters between the starts of two consecutive windows
    /// </summary>
    public const int WindowStride = 3500;

    /// <summary>
    /// maximum number of windows a node is split into
    /// </summary>
    public const int MaxWindows = 32;

    /// <summary>
    /// the separator placed between name and content in the composed string.
    /// double newline — markdown-natural paragraph boundary, unlikely to collide
    /// with names, clearly delimits "title" from "body".
    /// </summary>
    const string Separator = "\n\n";

    /// <summary>
    /// composes the embedding input from a node's name, content bytes, and content type.
    /// </summary>
    /// <param name="name">node name (nullable / empty allowed)</param>
    /// <param name="content">raw content bytes (nullable)</param>
    /// <param name="contentType">MIME type of the content (nullable)</param>
    /// <returns>
    /// the string to embed (≤<see cref="MaxLength"/> chars), or null when both name and
    /// content are empty/non-text (caller must write Embedding = null in that case).
    /// </returns>
    public static string Compose(string name, byte[] content, string contentType) {
        bool hasName = !string.IsNullOrWhiteSpace(name);
        bool hasTextContent = HasTextContent(content, contentType);

        if (!hasName && !hasTextContent)
            return null;

        if (hasName && hasTextContent) {
            string contentText = Encoding.UTF8.GetString(content);
            int contentBudget = Math.Max(0, MaxLength - name.Length - Separator.Length);
            if (contentText.Length > contentBudget)
                contentText = contentText[..contentBudget];
            string composed = name + Separator + contentText;
            return composed.Length > MaxLength ? composed[..MaxLength] : composed;
        }

        if (hasName) {
            string trimmedName = name.Length > MaxLength ? name[..MaxLength] : name;
            return trimmedName;
        }

        string text = Encoding.UTF8.GetString(content);
        return text.Length > MaxLength ? text[..MaxLength] : text;
    }

    /// <summary>
    /// composes the texts to embed for a node: exactly <see cref="Compose"/> when its untruncated input fits into <see cref="MaxLength"/>,
    /// otherwise at most <see cref="MaxWindows"/> overlapping content windows, each prefixed with the name
    /// </summary>
    /// <param name="name">node name (nullable / empty allowed)</param>
    /// <param name="content">raw content bytes (nullable)</param>
    /// <param name="contentType">MIME type of the content (nullable)</param>
    /// <returns>texts to embed, empty when there is no embeddable surface</returns>
    public static string[] ComposeWindows(string name, byte[] content, string contentType) {
        string composed = Compose(name, content, contentType);
        if (composed == null)
            return [];

        if (!HasTextContent(content, contentType))
            return [composed];

        string prefix = string.IsNullOrWhiteSpace(name) ? string.Empty : name + Separator;
        string contentText = Encoding.UTF8.GetString(content);
        int budget = MaxLength - prefix.Length;
        if (prefix.Length + contentText.Length <= MaxLength || budget <= 0)
            return [composed];

        return WindowStarts(contentText.Length)
               .Select(start => prefix + Window(contentText, start, budget))
               .ToArray();
    }

    static bool HasTextContent(byte[] content, string contentType) {
        return TextContentTypePredicate.IsText(contentType) && content != null && content.Length > 0;
    }

    static int[] WindowStarts(int length) {
        int naturalCount = length <= WindowStride ? 1 : (length - WindowStride + WindowStride - 1) / WindowStride;
        if (naturalCount <= MaxWindows)
            return Enumerable.Range(0, naturalCount).Select(index => index * WindowStride).ToArray();

        long span = length - WindowLength;
        return Enumerable.Range(0, MaxWindows)
                         .Select(index => (int) ((2 * index * span + (MaxWindows - 1)) / (2 * (MaxWindows - 1))))
                         .ToArray();
    }

    static string Window(string text, int start, int budget) {
        int from = SplitsSurrogatePair(text, start) ? start - 1 : start;
        int to = Math.Min(Math.Min(start + Math.Min(WindowLength, budget), text.Length), from + budget);
        if (SplitsSurrogatePair(text, to))
            to--;
        return text[from..to];
    }

    static bool SplitsSurrogatePair(string text, int boundary) {
        return boundary > 0 && boundary < text.Length && char.IsHighSurrogate(text[boundary - 1]) && char.IsLowSurrogate(text[boundary]);
    }
}
