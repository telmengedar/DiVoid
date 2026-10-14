using Backend.Services.Embeddings;

namespace Backend.Init;

/// <summary>
/// runs the <see cref="IEmbeddingModelGuard"/> at startup, after <see cref="DatabaseModelService"/> created the schema
/// </summary>
public class EmbeddingModelGuardService(IEmbeddingModelGuard guard, IEmbeddingProducer producer) : IHostedService {

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) {
        return guard.EnsureAsync(producer.Model);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) {
        return Task.CompletedTask;
    }
}
