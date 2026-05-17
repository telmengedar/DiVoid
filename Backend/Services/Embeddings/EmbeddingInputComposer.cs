using System.Text;

namespace Backend.Services.Embeddings;

/// <summary>
/// single source of truth for the v2 embedding composition policy.
/// given a node's name, content bytes, and content type, returns the string
/// to feed to the embedding model — or null when there is no embeddable surface.
///
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
        bool isTextContent = TextContentTypePredicate.IsText(contentType);
        bool hasTextContent = isTextContent && content != null && content.Length > 0;

        if (!hasName && !hasTextContent)
            return null;

        if (hasName && hasTextContent) {
            string contentText = Encoding.UTF8.GetString(content);
            int contentBudget = Math.Max(0, MaxLength - name.Length - Separator.Length);
            if (contentText.Length > contentBudget)
                contentText = contentText[..contentBudget];
            return name + Separator + contentText;
        }

        if (hasName) {
            string trimmedName = name.Length > MaxLength ? name[..MaxLength] : name;
            return trimmedName;
        }

        string text = Encoding.UTF8.GetString(content);
        return text.Length > MaxLength ? text[..MaxLength] : text;
    }
}
