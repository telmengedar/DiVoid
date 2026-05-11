namespace Backend.Services.Embeddings;

/// <summary>
/// singleton implementation of <see cref="IEmbeddingCapability"/>.
/// constructed once at startup with the result of the Database:Type check.
/// </summary>
public class EmbeddingCapability(bool isEnabled) : IEmbeddingCapability {

    /// <inheritdoc />
    public bool IsEnabled { get; } = isEnabled;
}
