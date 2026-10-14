using Backend.Services.Embeddings;

namespace Backend.Extensions.Startup;

/// <summary>
/// extensions for registering the embedding services
/// </summary>
public static class EmbeddingExtensions {

    /// <summary>
    /// registers the embedding capability, enabled for every database type except SQLite, the embedding producer and the model guard
    /// </summary>
    /// <param name="services">service collection to add the embedding services to</param>
    /// <param name="configuration">app configuration</param>
    /// <returns>service collection for fluent chaining</returns>
    public static IServiceCollection AddEmbedding(this IServiceCollection services, IConfiguration configuration) {
        bool embeddingEnabled = configuration["Database:Type"] != "Sqlite";
        services.AddSingleton<IEmbeddingCapability>(new EmbeddingCapability(embeddingEnabled));
        services.AddSingleton<IEmbeddingProducer, PostgresFunctionEmbeddingProducer>();
        services.AddTransient<IEmbeddingModelGuard, EmbeddingModelGuard>();
        return services;
    }
}
