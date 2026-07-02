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
    /// content-type prefixes that qualify as embeddable text.
    /// consumed identically by the C# gate (<see cref="IsText"/>) and the SQL gate
    /// (<see cref="GoogleMlEmbeddingProvider.BuildEmbeddingUpdate"/>): each entry is used
    /// as a case-insensitive LIKE prefix so the two paths cannot drift.
    ///
    /// "text/" covers all text/* subtypes (text/plain, text/markdown, text/html, etc.).
    /// "application/json" and "application/xml" match those types regardless of any
    /// charset suffix — "application/json; charset=utf-8" starts with "application/json".
    ///
    /// note: this deliberately narrows the previous allowlist.
    /// application/x-yaml, application/yaml, application/javascript, and application/x-sh
    /// are dropped.  see §3a of the embedding-providers design doc (DiVoid #2597).
    /// </summary>
    public static readonly string[] TextPrefixes = [
        "text/",
        "application/json",
        "application/xml"
    ];

    /// <summary>
    /// returns true when <paramref name="contentType"/> denotes textual data that
    /// should be embedded.  matching is case-insensitive; charset and other suffixes
    /// (e.g. "text/plain; charset=utf-8", "application/json; charset=utf-8") are handled
    /// naturally by prefix matching — no stripping is required.
    /// </summary>
    /// <param name="contentType">raw Content-Type string from the request</param>
    /// <returns>true if the content should be embedded; false otherwise</returns>
    public static bool IsText(string contentType) {
        if (string.IsNullOrEmpty(contentType))
            return false;

        string lower = contentType.ToLowerInvariant();
        foreach (string prefix in TextPrefixes) {
            if (lower.StartsWith(prefix))
                return true;
        }
        return false;
    }
}
