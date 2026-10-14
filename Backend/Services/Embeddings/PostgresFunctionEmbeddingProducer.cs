using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Tokens;
using Pooshit.Ocelot.Tokens.Values;

namespace Backend.Services.Embeddings;

/// <summary>
/// produces vectors with the Postgres <c>embedding()</c> SQL function
/// </summary>
public class PostgresFunctionEmbeddingProducer : IEmbeddingProducer {

    /// <summary>
    /// model identifier passed to <c>embedding()</c>
    /// </summary>
    public const string ModelId = "gemini-embedding-001";

    /// <inheritdoc />
    public string Model => ModelId;

    /// <inheritdoc />
    public ISqlToken Embed(string text, EmbeddingPurpose purpose) {
        ISqlToken embedding = DB.CustomFunction("embedding", DB.Constant(ModelId), DB.Constant(text));
        ISqlToken vector = DB.Value<object>(v => DB.Cast(embedding, CastType.Vector));
        return DB.CustomFunction("subvector", vector, DB.Constant(1), DB.Constant(EmbeddingExpression.Dimensions));
    }
}
