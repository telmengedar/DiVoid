namespace Backend.Services.Embeddings;

/// <summary>
/// single source of truth for the composition policy constants consumed by BOTH
/// the C# composition path (<see cref="EmbeddingInputComposer"/>) and the SQL
/// composition path (<see cref="GoogleMlEmbeddingProvider.BuildEmbeddingUpdate"/>).
///
/// pure static holder — no I/O, no SQL, no DI.  the parity guard test
/// (<c>EmbeddingCompositionParityTests</c>) asserts that the two paths stay aligned
/// on every knob defined here.
/// </summary>
public static class EmbeddingCompositionPolicy {

    /// <summary>
    /// separator placed between name and content in the composed string.
    /// double newline — markdown-natural paragraph boundary.
    /// </summary>
    public const string Separator = "\n\n";

    /// <summary>
    /// maximum number of characters in the composed output string.
    /// both the C# composer and the SQL builder enforce this budget.
    /// </summary>
    public const int MaxLength = 8000;

    /// <summary>
    /// dimension of the pgvector <c>Embedding</c> column on <c>Node</c>.
    /// used in the <c>[Size(EmbeddingDimension)]</c> attribute on <c>Node.Embedding</c>
    /// and in the startup fail-closed gate that rejects a configured provider whose
    /// declared <c>Dimension</c> does not equal this value.
    /// </summary>
    public const int EmbeddingDimension = 3072;

    /// <summary>
    /// delegates to <see cref="TextContentTypePredicate.IsText"/> — single source
    /// of truth for which MIME types count as embeddable text.
    /// </summary>
    public static bool IsText(string contentType) => TextContentTypePredicate.IsText(contentType);

    /// <summary>
    /// delegates to <see cref="TextContentTypePredicate.ApplicationTextTypes"/> —
    /// the allowlist of application/* MIME types that qualify as embeddable text.
    /// exposed so SQL-side IN predicates can be built without duplicating the list.
    /// </summary>
    public static string[] ApplicationTextTypes => TextContentTypePredicate.ApplicationTextTypes;
}
