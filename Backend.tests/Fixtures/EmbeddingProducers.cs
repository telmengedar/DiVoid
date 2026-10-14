using Backend.Services.Embeddings;

namespace Backend.tests.Fixtures;

/// <summary>
/// producers shared by tests which construct embedding consumers directly
/// </summary>
public static class EmbeddingProducers
{
    /// <summary>
    /// the Postgres function producer, the one the GCP deployment runs with
    /// </summary>
    public static readonly IEmbeddingProducer Function = new PostgresFunctionEmbeddingProducer();
}
