namespace Backend.Models;

/// <summary>
/// registry of all <see cref="Metadata"/> names; each is <c>feature.item</c>, holds text and is written only by its owner
/// </summary>
public static class MetadataKeys {

    /// <summary>
    /// owner: embedding model guard. the model that produced every non-null <c>node.embedding</c>, as the producer's model identity
    /// </summary>
    public const string EmbeddingModel = "embedding.model";
}
