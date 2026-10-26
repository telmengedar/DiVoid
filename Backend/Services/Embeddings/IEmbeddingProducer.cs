using Pooshit.Ocelot.Tokens;

namespace Backend.Services.Embeddings;

/// <summary>
/// turns a text, or several texts together, into the SQL expressions which evaluate to their <see cref="EmbeddingExpression.Dimensions"/>-dimensional vector
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

    /// <summary>
    /// builds the vector expressions of several texts, producers which can do so start the work for all texts at once
    /// </summary>
    /// <param name="texts">texts to embed, not empty, no text null</param>
    /// <param name="purpose">whether the vectors are stored or compared against stored vectors</param>
    /// <param name="cancellationToken">cancels the call before any text is embedded</param>
    /// <returns>one expression per text in the order of <paramref name="texts"/>, each evaluating to the vector <see cref="Embed"/> yields for that text</returns>
    Task<ISqlToken[]> EmbedManyAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken cancellationToken);

    /// <summary>
    /// makes the producer ready to embed, acquiring and loading what it needs; throws when it can not be made ready
    /// </summary>
    /// <param name="cancellationToken">cancels the preparation</param>
    Task PrepareAsync(CancellationToken cancellationToken);
}
