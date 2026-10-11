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
    /// expression yielding the first <see cref="Dimensions"/> components of the embedding of <paramref name="text"/>
    /// </summary>
    /// <param name="text">token which resolves to the text to embed</param>
    /// <returns>vector expression, usable for column writes and as query vector</returns>
    public static ISqlToken OfText(ISqlToken text) {
        ISqlToken embedding = DB.CustomFunction("embedding", DB.Constant(TextContentTypePredicate.EmbeddingModel), text);
        ISqlToken vector = DB.Value<object>(v => DB.Cast(embedding, CastType.Vector));
        return DB.CustomFunction("subvector", vector, DB.Constant(1), DB.Constant(Dimensions));
    }

    /// <summary>
    /// expression yielding the vector stored for a list of texts: <see cref="OfText"/> for one text, the normalised sum of the normalised vectors of all texts otherwise
    /// </summary>
    /// <param name="texts">texts to embed, at least one</param>
    /// <returns>vector expression usable for column writes</returns>
    public static ISqlToken OfTexts(string[] texts) {
        if (texts == null || texts.Length == 0)
            throw new ArgumentException("at least one text is required", nameof(texts));

        if (texts.Length == 1)
            return OfText(DB.Constant(texts[0]));

        ISqlToken sum = texts
                        .Select(text => DB.CustomFunction("l2_normalize", OfText(DB.Constant(text))))
                        .Aggregate((total, next) => DB.CustomFunction("vector_add", total, next));
        return DB.CustomFunction("l2_normalize", sum);
    }
}
