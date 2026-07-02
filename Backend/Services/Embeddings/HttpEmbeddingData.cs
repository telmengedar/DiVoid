namespace Backend.Services.Embeddings;

/// <summary>
/// single embedding result from the OpenAI-compatible embeddings response.
/// </summary>
sealed class HttpEmbeddingData {
    public float[] Embedding { get; set; }
}
