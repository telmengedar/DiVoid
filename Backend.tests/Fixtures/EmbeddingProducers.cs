using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Pooshit.Ocelot.Tokens;

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

    /// <summary>
    /// builds the query vector expressions of a filter through the function producer, one per effective query
    /// </summary>
    /// <param name="filter">filter of the search</param>
    /// <returns>vector expression of each effective query of the filter, in order</returns>
    public static ISqlToken[] QueryVectorsOf(NodeFilter filter) {
        return filter.GetEffectiveQueries().Select(query => Function.Embed(query, EmbeddingPurpose.Query)).ToArray();
    }
}
