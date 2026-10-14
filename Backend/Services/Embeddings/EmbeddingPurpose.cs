namespace Backend.Services.Embeddings;

/// <summary>
/// what an embedded text is used for
/// </summary>
public enum EmbeddingPurpose {

    /// <summary>
    /// text stored in <c>node.embedding</c>
    /// </summary>
    Document,

    /// <summary>
    /// text compared against stored embeddings
    /// </summary>
    Query
}
