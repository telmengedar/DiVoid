using Backend.Services.Embeddings;

namespace Backend.Init;

/// <summary>
/// runs the <see cref="IEmbeddingModelGuard"/> at startup, after <see cref="DatabaseModelService"/> created the schema
/// </summary>
public class EmbeddingModelGuardService(IEmbeddingModelGuard guard) : IHostedService {

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) {
        return guard.EnsureAsync(TextContentTypePredicate.EmbeddingModel);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) {
        return Task.CompletedTask;
    }
}
