using Backend.Services.Embeddings;
using Backend.tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// LocalModel lane: compares token ids and vectors with the sentence-transformers reference, with the real model files
/// </summary>
[TestFixture, Parallelizable]
public class LocalModelParityTests {
    const int ReferenceEntries = 27;
    const double MinimumCosine = 0.999;

    static EmbeddingReference LoadedReference() {
        EmbeddingReference reference = EmbeddingReference.Load();
        Assert.That(reference.Entries, Has.Length.EqualTo(ReferenceEntries), "the reference fixture must hold every text in both forms");
        return reference;
    }


    [Test, Parallelizable]
    [Explicit("LocalModel lane - requires DIVOID_EMBEDDING_MODEL_DIR; runs on the gangolf Docker daemon only.")]
    [Category("LocalModel")]
    [Description("Every prompt of the reference fixture is split into exactly the token ids the sentence-transformers model received, begin and end tokens included.")]
    public async Task Encode_ReferencePrompts_MatchReferenceIds() {
        EmbeddingReference reference = LoadedReference();
        await using ServiceProvider provider = LocalModelEnvironment.Provider();
        await provider.GetRequiredService<IEmbeddingModelFiles>().EnsureAsync(CancellationToken.None);
        using EmbeddingTokenizer tokenizer = new(Path.Combine(LocalModelEnvironment.ModelDirectory, PinnedEmbeddingModel.TokenizerFile));

        Assert.Multiple(() => {
            foreach (EmbeddingReferenceEntry entry in reference.Entries)
                Assert.That(tokenizer.Encode(entry.Prompt), Is.EqualTo(entry.InputIds), $"{entry.Name}/{entry.Purpose}");
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
                double cosine = LocalModelEnvironment.Cosine(model.Embed(entry.Prompt), entry.Vector);
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
