using Backend.Services.Embeddings;
using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Tokens;

namespace Backend.tests.Fixtures;

/// <summary>
/// producer which records every request and answers with a marker function instead of the Postgres embedding function
/// </summary>
public class RecordingEmbeddingProducer : IEmbeddingProducer
{
    readonly List<(string Text, EmbeddingPurpose Purpose)> calls = [];

    /// <summary>
    /// name of the function the produced expressions call
    /// </summary>
    public const string MarkerFunction = "recorded_vector";

    /// <inheritdoc />
    public string Model => "recording";

    /// <summary>
    /// requests received so far, in order
    /// </summary>
    public (string Text, EmbeddingPurpose Purpose)[] Calls
    {
        get {
            lock (calls)
                return calls.ToArray();
        }
    }

    /// <inheritdoc />
    public ISqlToken Embed(string text, EmbeddingPurpose purpose)
    {
        lock (calls)
            calls.Add((text, purpose));
        return DB.CustomFunction(MarkerFunction, DB.Constant(text));
    }
}
