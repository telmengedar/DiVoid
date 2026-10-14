using Backend.Services.Embeddings;

namespace Backend.Init;

/// <summary>
/// runs the <see cref="IEmbeddingModelGuard"/> and prepares the <see cref="IEmbeddingProducer"/> at startup, after <see cref="DatabaseModelService"/> created the schema
/// </summary>
public class EmbeddingModelGuardService(IEmbeddingModelGuard guard, IEmbeddingProducer producer, IEmbeddingCapability embeddingCapability, ILogger<EmbeddingModelGuardService> logger) : IHostedService {

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken) {
        if (!embeddingCapability.IsEnabled)
            return;

        logger.LogInformation("event=embedding.producer model={Model}", producer.Model);
        await guard.EnsureAsync(producer.Model);
        await producer.PrepareAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) {
        return Task.CompletedTask;
    }
}
