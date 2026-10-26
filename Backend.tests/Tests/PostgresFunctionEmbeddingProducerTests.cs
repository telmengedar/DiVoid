using Backend.Services.Embeddings;
using Backend.tests.Fixtures;
using NUnit.Framework;
using Pooshit.Ocelot.Entities.Operations.Prepared;
using Pooshit.Ocelot.Tokens;

namespace Backend.tests.Tests;

/// <summary>
/// pins that the several-texts request of the Postgres function producer yields the expressions of single requests
/// </summary>
[TestFixture, Parallelizable]
public class PostgresFunctionEmbeddingProducerTests {

    [Test, Parallelizable]
    [Description("Each text gets the expression a single request yields for it, in the order of the texts.")]
    public async Task EmbedManyAsync_SeveralTexts_ReturnsSingleRequestExpressionsInOrder() {
        string[] texts = ["alpha", "beta", "gamma"];

        ISqlToken[] expressions = await new PostgresFunctionEmbeddingProducer().EmbedManyAsync(texts, EmbeddingPurpose.Query, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(expressions, Has.Length.EqualTo(3));
            for (int i = 0; i < texts.Length; i++) {
                PreparedOperation expected = ExpressionPreparer.Prepare(new PostgresFunctionEmbeddingProducer().Embed(texts[i], EmbeddingPurpose.Query));
                PreparedOperation actual = ExpressionPreparer.Prepare(expressions[i]);
                Assert.That(actual.CommandText, Is.EqualTo(expected.CommandText), texts[i]);
                Assert.That(actual.ConstantParameters, Is.EqualTo(expected.ConstantParameters), texts[i]);
            }
        });
    }

    [Test, Parallelizable]
    [Description("A cancelled token stops the call before any expression is built.")]
    public void EmbedManyAsync_CancelledToken_Throws() {
        using CancellationTokenSource source = new();
        source.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(() => new PostgresFunctionEmbeddingProducer().EmbedManyAsync(["a"], EmbeddingPurpose.Query, source.Token));
    }
}
