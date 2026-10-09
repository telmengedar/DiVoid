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
}
