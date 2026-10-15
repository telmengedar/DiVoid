using System.Text.RegularExpressions;
using Backend.Services.Embeddings;
using Pooshit.Ocelot.Tokens;

namespace Backend.tests.Fixtures;

/// <summary>
/// producer which hands deterministic bag-of-words vectors to Postgres as constants, so texts sharing words rank close
/// </summary>
public class HashedWordEmbeddingProducer : IEmbeddingProducer {
    /// <inheritdoc />
    public string Model => "hashed-words";

    /// <summary>
    /// computes the unit vector of a text: every lower case word adds one to the component its FNV-1a hash selects
    /// </summary>
    /// <param name="text">text with at least one word</param>
    /// <returns>vector of length one</returns>
    public static float[] Vector(string text) {
        float[] vector = new float[EmbeddingExpression.Dimensions];
        foreach (string word in Regex.Split(text.ToLowerInvariant(), @"\W+").Where(word => word.Length > 0))
            vector[(int)(Hash(word) % EmbeddingExpression.Dimensions)] += 1;
        return EmbeddingVector.Normalize(vector);
    }

    static uint Hash(string word) {
        uint hash = 2166136261;
        foreach (byte value in System.Text.Encoding.UTF8.GetBytes(word))
            hash = (hash ^ value) * 16777619;
        return hash;
    }

    /// <inheritdoc />
    public ISqlToken Embed(string text, EmbeddingPurpose purpose) => EmbeddingVector.ToExpression(Vector(text));

    /// <inheritdoc />
    public Task PrepareAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
