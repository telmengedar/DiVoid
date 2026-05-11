namespace Backend.Services.Embeddings;

/// <summary>
/// classifies a Content-Type string as text (embeddable) or non-text.
/// single source of truth for what "text" means in this codebase.
/// </summary>
public static class TextContentTypePredicate {

    /// <summary>
    /// the embedding model identifier shared by all call sites in this project.
    /// centralized here per §13 Decision 1 of the embeddings architecture doc.
    /// </summary>
    public const string EmbeddingModel = "gemini-embedding-001";

    /// <summary>
    /// application/* MIME types that qualify as embeddable text.
    /// exposed so callers can build SQL-side IN predicates without
    /// duplicating the allowlist.
    /// </summary>
    public static readonly string[] ApplicationTextTypes = [
        "application/json",
        "application/xml",
        "application/x-yaml",
        "application/yaml",
        "application/javascript",
        "application/x-sh"
    ];

    /// <summary>
    /// returns true when <paramref name="contentType"/> denotes textual data that
    /// should be embedded.  handles charset suffixes (e.g. "text/plain; charset=utf-8")
    /// by stripping the part after the first semicolon before matching.
    /// matching is case-insensitive.
    /// </summary>
    /// <param name="contentType">raw Content-Type string from the request</param>
    /// <returns>true if the content should be embedded; false otherwise</returns>
    public static bool IsText(string contentType) {
        if (string.IsNullOrEmpty(contentType))
            return false;

        // strip charset and other parameters (e.g. "text/plain; charset=utf-8" -> "text/plain")
        int semicolon = contentType.IndexOf(';');
        string mimeType = (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim().ToLowerInvariant();

        // text/* covers text/plain, text/markdown, text/html, text/csv, text/xml, etc.
        if (mimeType.StartsWith("text/"))
            return true;

        return mimeType switch {
            "application/json" => true,
            "application/xml" => true,
            "application/x-yaml" => true,
            "application/yaml" => true,
            "application/javascript" => true,
            "application/x-sh" => true,
            _ => false
        };
    }
}
