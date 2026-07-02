namespace Backend.Services.Embeddings;

/// <summary>
/// request body for the OpenAI-compatible <c>/v1/embeddings</c> endpoint.
/// </summary>
sealed class HttpEmbeddingsRequest {
    public string Input { get; set; }
    public string Model { get; set; }
    public int? Dimensions { get; set; }
}
