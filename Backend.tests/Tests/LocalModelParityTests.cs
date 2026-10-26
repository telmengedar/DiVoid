using Backend.Services.Embeddings;
using Backend.tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Pooshit.Ocelot.Tokens;

namespace Backend.tests.Tests;

/// <summary>
/// LocalModel lane: compares token ids and vectors with the sentence-transformers reference, with the real model files
/// </summary>
[TestFixture, Parallelizable]
public class LocalModelParityTests {
    const int ReferenceEntries = 29;
    const double MinimumCosine = 0.999;

    static string ProducerPrompt(EmbeddingReferenceEntry entry) {
        return EmbeddingPrompt.For(entry.Text, entry.Purpose == "query" ? EmbeddingPurpose.Query : EmbeddingPurpose.Document);
    }

    static EmbeddingReference LoadedReference() {
        EmbeddingReference reference = EmbeddingReference.Load();
        Assert.That(reference.Entries, Has.Length.EqualTo(ReferenceEntries), "the reference fixture must hold every text in both forms");
        return reference;
    }


    [Test, Parallelizable]
    [Explicit("LocalModel lane - requires DIVOID_EMBEDDING_MODEL_DIR; runs on the gangolf Docker daemon only.")]
    [Category("LocalModel")]
    [Description("Every text of the reference fixture, prompted the way the producer prompts it, is split into exactly the token ids the sentence-transformers model received, begin and end tokens included.")]
    public async Task Encode_ReferencePrompts_MatchReferenceIds() {
        EmbeddingReference reference = LoadedReference();
        await using ServiceProvider provider = LocalModelEnvironment.Provider();
        await provider.GetRequiredService<IEmbeddingModelFiles>().EnsureAsync(CancellationToken.None);
        using EmbeddingTokenizer tokenizer = new(Path.Combine(LocalModelEnvironment.ModelDirectory, PinnedEmbeddingModel.TokenizerFile));

        Assert.Multiple(() => {
            foreach (EmbeddingReferenceEntry entry in reference.Entries) {
                Assert.That(ProducerPrompt(entry), Is.EqualTo(entry.Prompt), $"prompt of {entry.Name}/{entry.Purpose}");
                Assert.That(tokenizer.Encode(ProducerPrompt(entry)), Is.EqualTo(entry.InputIds), $"{entry.Name}/{entry.Purpose}");
            }
        });
    }


    [Test, Parallelizable]
    [Explicit("LocalModel lane - requires DIVOID_EMBEDDING_MODEL_DIR; runs on the gangolf Docker daemon only.")]
    [Category("LocalModel")]
    [Description("Every prompt of the reference fixture is embedded to within a cosine of 0.999 of the vector the fp32 PyTorch reference produced.")]
    public async Task Embed_ReferencePrompts_MatchReferenceVectors() {
        EmbeddingReference reference = LoadedReference();
        await using ServiceProvider provider = await LocalModelEnvironment.PreparedProvider();
        ILocalEmbeddingModel model = provider.GetRequiredService<ILocalEmbeddingModel>();

        Assert.Multiple(() => {
            foreach (EmbeddingReferenceEntry entry in reference.Entries) {
                double cosine = LocalModelEnvironment.Cosine(model.Embed(ProducerPrompt(entry)), entry.Vector);
                TestContext.Progress.WriteLine($"cosine {entry.Name}/{entry.Purpose} = {cosine:F6}");
                Assert.That(cosine, Is.GreaterThanOrEqualTo(MinimumCosine), $"{entry.Name}/{entry.Purpose}");
            }
        });
    }


    [Test, Parallelizable]
    [Explicit("LocalModel lane - requires DIVOID_EMBEDDING_MODEL_DIR; runs on the gangolf Docker daemon only.")]
    [Category("LocalModel")]
    [Description("Concurrent requests share one session and one tokenizer and each gets the vector a sequential request gets.")]
    public async Task Embed_ConcurrentRequests_MatchSequentialResults() {
        EmbeddingReference reference = LoadedReference();
        await using ServiceProvider provider = await LocalModelEnvironment.PreparedProvider();
        ILocalEmbeddingModel model = provider.GetRequiredService<ILocalEmbeddingModel>();
        EmbeddingReferenceEntry[] entries = reference.Entries.Where(entry => entry.Name != "full-window").ToArray();
        float[][] sequential = entries.Select(entry => model.Embed(entry.Prompt)).ToArray();

        float[][] concurrent = await Task.WhenAll(Enumerable.Repeat(entries, 3).SelectMany(batch => batch).Select(entry => Task.Run(() => model.Embed(entry.Prompt))));

        Assert.Multiple(() => {
            for (int i = 0; i < concurrent.Length; i++)
                Assert.That(LocalModelEnvironment.Cosine(concurrent[i], sequential[i % entries.Length]), Is.GreaterThan(0.99999), $"{entries[i % entries.Length].Name}/{entries[i % entries.Length].Purpose}");
        });
    }


    [Test, Parallelizable]
    [Explicit("LocalModel lane - requires DIVOID_EMBEDDING_MODEL_DIR; runs on the gangolf Docker daemon only.")]
    [Category("LocalModel")]
    [Description("The vectors of several texts embedded together lie within 1e-5 of the vectors the same texts get one after another.")]
    public async Task EmbedManyAsync_ReferenceTexts_MatchSequentialEmbedWithinSimilarityBound() {
        EmbeddingReference reference = LoadedReference();
        await using ServiceProvider provider = await LocalModelEnvironment.PreparedProvider();
        IEmbeddingProducer producer = provider.GetRequiredService<IEmbeddingProducer>();
        string[] texts = reference.Entries.Where(entry => entry.Name != "full-window").Select(entry => entry.Text).Distinct().Take(10).ToArray();
        float[][] sequential = texts.Select(text => (float[]) ExpressionPreparer.Prepare(producer.Embed(text, EmbeddingPurpose.Query)).ConstantParameters[0]).ToArray();

        ISqlToken[] together = await producer.EmbedManyAsync(texts, EmbeddingPurpose.Query, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(together, Has.Length.EqualTo(texts.Length));
            for (int i = 0; i < texts.Length; i++) {
                float[] vector = (float[]) ExpressionPreparer.Prepare(together[i]).ConstantParameters[0];
                double distance = Math.Sqrt(vector.Zip(sequential[i], (a, b) => (double) (a - b) * (a - b)).Sum());
                Assert.That(distance, Is.LessThan(1e-5), texts[i]);
            }
        });
    }


    [Test, Parallelizable]
    [Explicit("LocalModel lane - requires DIVOID_EMBEDDING_MODEL_DIR; runs on the gangolf Docker daemon only.")]
    [Category("LocalModel")]
    [Description("A text containing the placeholder of a media input is embedded instead of failing in the graph.")]
    [TestCase("<|image|>")]
    [TestCase("<|audio|>")]
    [TestCase("<|video|>")]
    public async Task Embed_MediaPlaceholderInText_DoesNotFailTheModel(string placeholder) {
        await using ServiceProvider provider = await LocalModelEnvironment.PreparedProvider();
        IEmbeddingProducer producer = provider.GetRequiredService<IEmbeddingProducer>();

        Assert.DoesNotThrow(() => producer.Embed($"notes about the {placeholder} syntax", EmbeddingPurpose.Document));
    }
}
