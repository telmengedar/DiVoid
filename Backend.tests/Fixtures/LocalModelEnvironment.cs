using Backend.Extensions.Startup;
using Backend.Services.Embeddings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.tests.Fixtures;

/// <summary>
/// the real local embedding producer, registered the way the host registers it, on the model directory the LocalModel lane provides
/// </summary>
public static class LocalModelEnvironment {
    /// <summary>
    /// directory named by <c>DIVOID_EMBEDDING_MODEL_DIR</c>, in which the model files are kept between runs
    /// </summary>
    public static string ModelDirectory => Environment.GetEnvironmentVariable("DIVOID_EMBEDDING_MODEL_DIR")
                                           ?? throw new InvalidOperationException("DIVOID_EMBEDDING_MODEL_DIR is not set");

    /// <summary>
    /// registers the embedding services for a Postgres database with the default producer, the way the host does
    /// </summary>
    /// <returns>provider owning the unprepared producer</returns>
    public static ServiceProvider Provider() {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Database:Type"] = "PostgreSQL",
            ["Embedding:ModelPath"] = ModelDirectory
        }).Build();
        return new ServiceCollection().AddLogging().AddEmbedding(configuration).BuildServiceProvider();
    }

    /// <summary>
    /// registers the embedding services and prepares the producer, which downloads missing model files and loads the model
    /// </summary>
    /// <returns>provider owning the prepared producer</returns>
    public static async Task<ServiceProvider> PreparedProvider() {
        ServiceProvider provider = Provider();
        await provider.GetRequiredService<IEmbeddingProducer>().PrepareAsync(CancellationToken.None);
        return provider;
    }

    /// <summary>
    /// cosine of the angle between two vectors of length one
    /// </summary>
    /// <param name="first">first vector</param>
    /// <param name="second">second vector, as long as <paramref name="first"/></param>
    /// <returns>dot product of both vectors</returns>
    public static double Cosine(float[] first, float[] second) {
        return first.Zip(second, (a, b) => (double)a * b).Sum();
    }
}
