using Backend.Services.Embeddings;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// pins the cut of a token sequence to the context of the model
/// </summary>
[TestFixture, Parallelizable]
public class EmbeddingTokenizerTruncateTests {

    [Test, Parallelizable]
    [Description("Tokens beyond the context of 8192 are dropped from the end.")]
    public void Truncate_LongerThanContext_KeepsFirstMaxTokens() {
        int[] ids = Enumerable.Range(0, EmbeddingTokenizer.MaxTokens + 5).ToArray();

        int[] truncated = EmbeddingTokenizer.Truncate(ids);

        Assert.That(truncated, Is.EqualTo(ids.Take(EmbeddingTokenizer.MaxTokens)));
    }


    [Test, Parallelizable]
    [Description("A sequence of exactly the context length is not cut.")]
    public void Truncate_ExactlyContext_ReturnsAllTokens() {
        int[] ids = Enumerable.Range(0, EmbeddingTokenizer.MaxTokens).ToArray();

        Assert.That(EmbeddingTokenizer.Truncate(ids), Is.EqualTo(ids));
    }


    [Test, Parallelizable]
    [Description("A shorter sequence is returned as it is.")]
    public void Truncate_ShorterThanContext_ReturnsAllTokens() {
        int[] ids = [2, 10, 20, 1];

        Assert.That(EmbeddingTokenizer.Truncate(ids), Is.EqualTo(ids));
    }


    [Test, Parallelizable]
    [Description("The context of the model is 8192 tokens.")]
    public void MaxTokens_Always_Is8192() {
        Assert.That(EmbeddingTokenizer.MaxTokens, Is.EqualTo(8192));
    }
}
