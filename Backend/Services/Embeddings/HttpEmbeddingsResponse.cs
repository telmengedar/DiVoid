namespace Backend.Services.Embeddings;

/// <summary>
/// response body from the OpenAI-compatible <c>/v1/embeddings</c> endpoint.
/// </summary>
sealed class HttpEmbeddingsResponse {
    public HttpEmbeddingData[] Data { get; set; }
}
