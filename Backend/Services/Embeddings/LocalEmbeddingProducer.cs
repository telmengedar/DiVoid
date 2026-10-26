using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Pooshit.Ocelot.Tokens;

namespace Backend.Services.Embeddings;

/// <summary>
/// produces vectors with EmbeddingGemma 2 running in this process
/// </summary>
public class LocalEmbeddingProducer(IEmbeddingModelFiles files, ILocalEmbeddingModel model, ILogger<LocalEmbeddingProducer> logger) : IEmbeddingProducer {

    /// <summary>
    /// identity of the vector space of EmbeddingGemma 2
    /// </summary>
    public const string ModelId = "embeddinggemma-2";

    const string SelfCheckQuery = "which embedding model is installed";

    /// <inheritdoc />
    public string Model => ModelId;

    /// <inheritdoc />
    public ISqlToken Embed(string text, EmbeddingPurpose purpose) {
        return EmbeddingVector.ToExpression(model.Embed(EmbeddingPrompt.For(text, purpose)));
    }

    /// <inheritdoc />
    public async Task<ISqlToken[]> EmbedManyAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.WhenAll(texts.Select(text => Task.Run(() => Embed(text, purpose), CancellationToken.None)));
    }

    /// <inheritdoc />
    public async Task PrepareAsync(CancellationToken cancellationToken) {
        await files.EnsureAsync(cancellationToken);

        Stopwatch watch = Stopwatch.StartNew();
        model.Load();
        long loadMilliseconds = watch.ElapsedMilliseconds;

        watch.Restart();
        model.Embed(EmbeddingPrompt.For(SelfCheckQuery, EmbeddingPurpose.Query));
        logger.LogInformation("event=embedding.model.ready load_ms={LoadMs} self_check_ms={SelfCheckMs}", loadMilliseconds, watch.ElapsedMilliseconds);
    }
}
