using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Tokens.Values;
using Pooshit.Ocelot.Tokens;

namespace Backend.Services.Embeddings;

/// <summary>
/// builds the SQL expression which turns a text into the vector stored in and compared against <c>node.embedding</c>
/// </summary>
public static class EmbeddingExpression {

    /// <summary>
    /// number of leading components of a full embedding which are kept, equals the dimension of the <c>node.embedding</c> column
    /// </summary>
    public const int Dimensions = 768;

    /// <summary>
    /// expression yielding the vector stored for a list of texts: the producer's vector for one text, the normalised sum of the normalised vectors of all texts otherwise
    /// </summary>
    /// <param name="texts">texts to embed, at least one</param>
    /// <param name="producer">producer of the per-text vectors</param>
    /// <returns>vector expression usable for column writes</returns>
    public static ISqlToken OfTexts(string[] texts, IEmbeddingProducer producer) {
        if (texts == null || texts.Length == 0)
            throw new ArgumentException("at least one text is required", nameof(texts));

        if (texts.Length == 1)
            return producer.Embed(texts[0], EmbeddingPurpose.Document);

        ISqlToken sum = texts
                        .Select(text => DB.CustomFunction("l2_normalize", producer.Embed(text, EmbeddingPurpose.Document)))
                        .Aggregate((total, next) => DB.CustomFunction("vector_add", total, next));
        return DB.CustomFunction("l2_normalize", sum);
    }
}
