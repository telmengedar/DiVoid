using Backend.Services.Embeddings;
using Pooshit.Ocelot.Tokens;

namespace Backend.tests.Fixtures;

/// <summary>Coordinates the first concurrent document embeddings.</summary>
public sealed class BarrierEmbeddingProducer(int participants) : IEmbeddingProducer, IDisposable {
    readonly CountdownEvent arrivals = new(participants);
    readonly IEmbeddingProducer producer = new HashedWordEmbeddingProducer();
    int calls;

    /// <inheritdoc />
    public string Model => producer.Model;

    /// <summary>Number of document embedding requests.</summary>
    public int Calls => Volatile.Read(ref calls);

    /// <inheritdoc />
    public ISqlToken Embed(string text, EmbeddingPurpose purpose) {
        if (purpose == EmbeddingPurpose.Document && Interlocked.Increment(ref calls) <= participants) {
            arrivals.Signal();
            if (!arrivals.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Concurrent embedding requests did not arrive");
        }
        return producer.Embed(text, purpose);
    }

    /// <inheritdoc />
    public Task<ISqlToken[]> EmbedManyAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken cancellationToken) =>
        Task.FromResult(texts.Select(text => Embed(text, purpose)).ToArray());

    /// <inheritdoc />
    public Task PrepareAsync(CancellationToken cancellationToken) => producer.PrepareAsync(cancellationToken);

    /// <inheritdoc />
    public void Dispose() => arrivals.Dispose();
}
