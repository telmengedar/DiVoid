using Backend.Extensions.Startup;
using Backend.Services.Embeddings;
using Backend.tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Pooshit.Http;

namespace Backend.tests.Tests;

/// <summary>
/// pins which embedding producer the registration selects and that selecting it touches no model file
/// </summary>
[TestFixture, Parallelizable]
public class EmbeddingRegistrationTests {

    static IConfiguration Configuration(string databaseType, string? producer = null, string? modelPath = null) {
        Dictionary<string, string?> settings = new() { ["Database:Type"] = databaseType };
        if (producer != null)
            settings["Embedding:Producer"] = producer;
        if (modelPath != null)
            settings["Embedding:ModelPath"] = modelPath;
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    static ServiceProvider Provider(IConfiguration configuration) {
        return new ServiceCollection().AddLogging().AddEmbedding(configuration).BuildServiceProvider();
    }


    [Test, Parallelizable]
    [Description("A database without a configured producer embeds locally, which is the default for everyone but GCP.")]
    public void AddEmbedding_ProducerUnset_RegistersLocalProducer() {
        using ServiceProvider provider = Provider(Configuration("PostgreSQL"));

        IEmbeddingProducer producer = provider.GetRequiredService<IEmbeddingProducer>();

        Assert.Multiple(() => {
            Assert.That(producer, Is.TypeOf<LocalEmbeddingProducer>());
            Assert.That(producer.Model, Is.EqualTo("embeddinggemma-2"));
        });
    }


    [Test, Parallelizable]
    [Description("The producer name is matched without regard to case.")]
    [TestCase("Local")]
    [TestCase("local")]
    [TestCase("LOCAL")]
    public void AddEmbedding_LocalInAnyCase_RegistersLocalProducer(string producer) {
        using ServiceProvider provider = Provider(Configuration("PostgreSQL", producer));

        Assert.That(provider.GetRequiredService<IEmbeddingProducer>(), Is.TypeOf<LocalEmbeddingProducer>());
    }


    [Test, Parallelizable]
    [Description("PostgresFunction keeps the GCP deployment on the embedding() SQL function.")]
    [TestCase("PostgresFunction")]
    [TestCase("postgresfunction")]
    public void AddEmbedding_PostgresFunction_RegistersFunctionProducer(string producer) {
        using ServiceProvider provider = Provider(Configuration("PostgreSQL", producer));

        IEmbeddingProducer registered = provider.GetRequiredService<IEmbeddingProducer>();

        Assert.Multiple(() => {
            Assert.That(registered, Is.TypeOf<PostgresFunctionEmbeddingProducer>());
            Assert.That(registered.Model, Is.EqualTo("gemini-embedding-001"));
        });
    }


    [Test, Parallelizable]
    [Description("An unknown producer name refuses startup and names the accepted values.")]
    public void AddEmbedding_UnknownProducer_ThrowsNamingAcceptedValues() {
        InvalidOperationException? exception = Assert.Throws<InvalidOperationException>(() => Provider(Configuration("PostgreSQL", "Ollama")));

        Assert.Multiple(() => {
            Assert.That(exception!.Message, Does.Contain("Ollama"));
            Assert.That(exception.Message, Does.Contain("Local"));
            Assert.That(exception.Message, Does.Contain("PostgresFunction"));
        });
    }


    [Test, Parallelizable]
    [Description("SQLite has no embeddings, so the producer setting is not read and an invalid value does not matter.")]
    [TestCase(null)]
    [TestCase("Local")]
    [TestCase("Ollama")]
    public void AddEmbedding_Sqlite_IgnoresProducerSetting(string? producer) {
        using ServiceProvider provider = Provider(Configuration("Sqlite", producer));

        Assert.Multiple(() => {
            Assert.That(provider.GetRequiredService<IEmbeddingProducer>(), Is.TypeOf<PostgresFunctionEmbeddingProducer>());
            Assert.That(provider.GetRequiredService<IEmbeddingCapability>().IsEnabled, Is.False);
        });
    }


    [Test, Parallelizable]
    [Description("Building the local producer reads and creates nothing, so a host without model files can start.")]
    public void AddEmbedding_LocalProducerResolved_TouchesNoFile() {
        string modelPath = Path.Combine(Path.GetTempPath(), "divoid-model-" + Guid.NewGuid().ToString("N"));
        using ServiceProvider provider = Provider(Configuration("PostgreSQL", "Local", modelPath));

        provider.GetRequiredService<IEmbeddingProducer>();
        provider.GetRequiredService<IEmbeddingModelFiles>();

        Assert.That(Directory.Exists(modelPath), Is.False);
    }


    [Test, Parallelizable]
    [Description("The model files are kept in the configured model path.")]
    public void AddEmbedding_ModelPathSet_AcquiresFilesInConfiguredDirectory() {
        using TemporaryDirectory modelPath = new(false);
        Mock<IHttpService> http = new();
        http.Setup(h => h.Get<HttpResponseMessage>(It.IsAny<string>(), It.IsAny<HttpOptions>())).ThrowsAsync(new HttpRequestException("offline"));
        using ServiceProvider provider = new ServiceCollection().AddLogging().AddSingleton(http.Object).AddEmbedding(Configuration("PostgreSQL", "Local", modelPath.Path)).BuildServiceProvider();

        InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRequiredService<IEmbeddingModelFiles>().EnsureAsync(CancellationToken.None));

        Assert.That(exception!.Message, Does.Contain(Path.Combine(modelPath.Path, "model_quantized.onnx")));
    }


    [Test, Parallelizable]
    [Description("Without a model path the files go to models/embeddinggemma-2 below the working directory.")]
    public void AddEmbedding_ModelPathUnset_AcquiresFilesBelowWorkingDirectory() {
        Mock<IHttpService> http = new();
        http.Setup(h => h.Get<HttpResponseMessage>(It.IsAny<string>(), It.IsAny<HttpOptions>())).ThrowsAsync(new HttpRequestException("offline"));
        string expected = Path.GetFullPath(Path.Combine("models", "embeddinggemma-2"));
        using ServiceProvider provider = new ServiceCollection().AddLogging().AddSingleton(http.Object).AddEmbedding(Configuration("PostgreSQL")).BuildServiceProvider();
        try {
            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRequiredService<IEmbeddingModelFiles>().EnsureAsync(CancellationToken.None));

            Assert.That(exception!.Message, Does.Contain(Path.Combine(expected, "model_quantized.onnx")));
        } finally {
            if (Directory.Exists(expected))
                Directory.Delete(expected, true);
        }
    }


    [Test, Parallelizable]
    [Description("A host on SQLite starts and serves with an empty model directory and acquires nothing.")]
    public async Task Startup_SqliteWithEmptyModelDirectory_ServesWithoutAcquiringModelFiles() {
        string modelPath = Path.Combine(Path.GetTempPath(), "divoid-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(modelPath);
        try {
            using WebApplicationFactory<Program> factory = TestSetup.CreateTestFactory().WithWebHostBuilder(builder => {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Embedding:ModelPath"] = modelPath }));
            });

            using HttpClient client = factory.CreateClient();
            HttpResponseMessage response = await client.GetAsync("/api/health");

            Assert.Multiple(() => {
                Assert.That(response.IsSuccessStatusCode, Is.True);
                Assert.That(Directory.GetFileSystemEntries(modelPath), Is.Empty);
            });
        } finally {
            Directory.Delete(modelPath, true);
        }
    }
}
