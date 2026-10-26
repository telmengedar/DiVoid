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
    readonly List<string> events = [];
    readonly List<(string[] Texts, EmbeddingPurpose Purpose)> manyCalls = [];

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

    /// <summary>
    /// several-texts requests received so far, in order
    /// </summary>
    public (string[] Texts, EmbeddingPurpose Purpose)[] ManyCalls {
        get {
            lock (calls)
                return manyCalls.ToArray();
        }
    }

    /// <summary>
    /// <c>prepare</c>, <c>embed</c> and <c>embed-many</c> in the order they were requested
    /// </summary>
    public string[] Events {
        get {
            lock (calls)
                return events.ToArray();
        }
    }

    /// <inheritdoc />
    public ISqlToken Embed(string text, EmbeddingPurpose purpose)
    {
        lock (calls) {
            calls.Add((text, purpose));
            events.Add("embed");
        }
        return DB.CustomFunction(MarkerFunction, DB.Constant(text));
    }

    /// <inheritdoc />
    public Task<ISqlToken[]> EmbedManyAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken cancellationToken)
    {
        lock (calls) {
            manyCalls.Add((texts.ToArray(), purpose));
            events.Add("embed-many");
        }
        return Task.FromResult(texts.Select(text => (ISqlToken) DB.CustomFunction(MarkerFunction, DB.Constant(text))).ToArray());
    }

    /// <inheritdoc />
    public Task PrepareAsync(CancellationToken cancellationToken) {
        lock (calls)
            events.Add("prepare");
        return Task.CompletedTask;
    }
}
