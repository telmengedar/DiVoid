using Backend.Services.Embeddings;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pooshit.Http;

namespace Backend.Extensions.Startup;

/// <summary>
/// extensions for registering the embedding services
/// </summary>
public static class EmbeddingExtensions {
    const string LocalProducer = "Local";
    const string PostgresFunctionProducer = "PostgresFunction";
    const string DefaultModelPath = "models/embeddinggemma-2";
    static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// registers the embedding capability, enabled for every database type except SQLite, the embedding producer and the model guard
    /// </summary>
    /// <param name="services">service collection to add the embedding services to</param>
    /// <param name="configuration">app configuration</param>
    /// <returns>service collection for fluent chaining</returns>
    /// <exception cref="InvalidOperationException"><c>Embedding:Producer</c> is neither <c>Local</c> nor <c>PostgresFunction</c></exception>
    public static IServiceCollection AddEmbedding(this IServiceCollection services, IConfiguration configuration) {
        bool embeddingEnabled = configuration["Database:Type"] != "Sqlite";
        services.AddSingleton<IEmbeddingCapability>(new EmbeddingCapability(embeddingEnabled));

        if (embeddingEnabled && UsesLocalProducer(configuration["Embedding:Producer"]))
            AddLocalProducer(services, Path.GetFullPath(configuration["Embedding:ModelPath"] ?? DefaultModelPath));
        else
            services.AddSingleton<IEmbeddingProducer, PostgresFunctionEmbeddingProducer>();

        services.AddTransient<IEmbeddingModelGuard, EmbeddingModelGuard>();
        return services;
    }

    static bool UsesLocalProducer(string producer) {
        if (string.IsNullOrEmpty(producer) || producer.Equals(LocalProducer, StringComparison.OrdinalIgnoreCase))
            return true;
        if (producer.Equals(PostgresFunctionProducer, StringComparison.OrdinalIgnoreCase))
            return false;

        throw new InvalidOperationException($"unknown Embedding:Producer '{producer}', accepted values are {LocalProducer} and {PostgresFunctionProducer}");
    }

    static void AddLocalProducer(IServiceCollection services, string modelDirectory) {
        services.TryAddSingleton<IHttpService>(_ => new HttpService { Timeout = DownloadTimeout });
        services.AddSingleton<IEmbeddingModelFiles>(provider => new EmbeddingModelFiles(provider.GetRequiredService<IHttpService>(), provider.GetRequiredService<ILogger<EmbeddingModelFiles>>(), modelDirectory, PinnedEmbeddingModel.Files));
        services.AddSingleton<ILocalEmbeddingModel>(_ => new LocalEmbeddingModel(modelDirectory));
        services.AddSingleton<IEmbeddingProducer, LocalEmbeddingProducer>();
    }
}
