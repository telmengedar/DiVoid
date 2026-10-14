using Pooshit.Ocelot.Tokens;

namespace Backend.Services.Embeddings;

/// <summary>
/// turns a single text into the SQL expression which evaluates to its <see cref="EmbeddingExpression.Dimensions"/>-dimensional vector
/// </summary>
public interface IEmbeddingProducer {

    /// <summary>
    /// identity of the vector space the produced vectors live in
    /// </summary>
    string Model { get; }

    /// <summary>
    /// builds the vector expression of a text
    /// </summary>
    /// <param name="text">text to embed, not null</param>
    /// <param name="purpose">whether the vector is stored or compared against stored vectors</param>
    /// <returns>expression evaluating to a vector, never null</returns>
    ISqlToken Embed(string text, EmbeddingPurpose purpose);
}
