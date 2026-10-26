using Backend.Services.Embeddings;
using Backend.tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Pooshit.Ocelot.Entities.Operations.Prepared;
using Pooshit.Ocelot.Tokens;

namespace Backend.tests.Tests;

/// <summary>
/// pins how the local producer prefixes its prompts, hands the model vector to SQL and prepares itself, against a faked model
/// </summary>
[TestFixture, Parallelizable]
public class LocalEmbeddingProducerTests {
    static readonly float[] ModelVector = Enumerable.Range(0, EmbeddingExpression.Dimensions).Select(i => (i + 1) / 1000f).ToArray();

    static Mock<ILocalEmbeddingModel> ModelAnswering(float[] vector) {
        Mock<ILocalEmbeddingModel> model = new();
        model.Setup(m => m.Embed(It.IsAny<string>())).Returns(vector);
        return model;
    }

    static LocalEmbeddingProducer ProducerOf(ILocalEmbeddingModel model, IEmbeddingModelFiles? files = null) {
        return new LocalEmbeddingProducer(files ?? Mock.Of<IEmbeddingModelFiles>(), model, NullLogger<LocalEmbeddingProducer>.Instance);
    }


    [Test, Parallelizable]
    [Description("A stored text is embedded with the document prefix of the model.")]
    public void Embed_Document_PrefixesTitleNone() {
        Mock<ILocalEmbeddingModel> model = ModelAnswering(ModelVector);

        ProducerOf(model.Object).Embed("some text", EmbeddingPurpose.Document);

        model.Verify(m => m.Embed("title: none | text: some text"), Times.Once);
    }


    [Test, Parallelizable]
    [Description("A search text is embedded with the query prefix of the model.")]
    public void Embed_Query_PrefixesSearchResult() {
        Mock<ILocalEmbeddingModel> model = ModelAnswering(ModelVector);

        ProducerOf(model.Object).Embed("some text", EmbeddingPurpose.Query);

        model.Verify(m => m.Embed("task: search result | query: some text"), Times.Once);
    }


    [Test, Parallelizable]
    [Description("The placeholders of media inputs are broken up, because the model fails with an error when a text contains one.")]
    [TestCase("<|image|>", "< |image|>")]
    [TestCase("<|audio|>", "< |audio|>")]
    [TestCase("<|video|>", "< |video|>")]
    public void Embed_MediaPlaceholderInText_IsBrokenUp(string placeholder, string embedded) {
        Mock<ILocalEmbeddingModel> model = ModelAnswering(ModelVector);

        ProducerOf(model.Object).Embed($"before {placeholder} after {placeholder}", EmbeddingPurpose.Document);

        model.Verify(m => m.Embed($"title: none | text: before {embedded} after {embedded}"), Times.Once);
    }


    [Test, Parallelizable]
    [Description("Special tokens other than the media placeholders are embedded as the text they are.")]
    public void Embed_OtherSpecialTokenLiterals_AreKept() {
        Mock<ILocalEmbeddingModel> model = ModelAnswering(ModelVector);

        ProducerOf(model.Object).Embed("a <bos> b <|image> c <image|> d", EmbeddingPurpose.Query);

        model.Verify(m => m.Embed("task: search result | query: a <bos> b <|image> c <image|> d"), Times.Once);
    }


    [Test, Parallelizable]
    [Description("The vector of the model reaches SQL as one bound constant cast to vector, in component order.")]
    public void Embed_ModelVector_RendersCastOfOneBoundParameter() {
        PreparedOperation prepared = ExpressionPreparer.Prepare(ProducerOf(ModelAnswering(ModelVector).Object).Embed("some text", EmbeddingPurpose.Document));

        Assert.Multiple(() => {
            Assert.That(System.Text.RegularExpressions.Regex.Replace(prepared.CommandText, @"\s+", " "), Does.Contain("SET \"embedding\" = ( @1 ::vector ) WHERE"));
            Assert.That(prepared.ConstantParameters, Has.Length.EqualTo(2));
            Assert.That(prepared.ConstantParameters[0], Is.EqualTo(ModelVector));
        });
    }


    [Test, Parallelizable]
    [Description("The model runs for all texts at the same time, which a sequential loop can not satisfy.")]
    public async Task EmbedManyAsync_SeveralTexts_RunsModelCallsConcurrently() {
        const int count = 4;
        using CountdownEvent inFlight = new(count);
        int allArrived = 0;
        Mock<ILocalEmbeddingModel> model = new();
        model.Setup(m => m.Embed(It.IsAny<string>())).Returns(() => {
            inFlight.Signal();
            if (inFlight.Wait(TimeSpan.FromSeconds(10)))
                Interlocked.Increment(ref allArrived);
            return ModelVector;
        });

        await ProducerOf(model.Object).EmbedManyAsync(["a", "b", "c", "d"], EmbeddingPurpose.Query, CancellationToken.None);

        Assert.That(allArrived, Is.EqualTo(count));
    }


    [Test, Parallelizable]
    [Description("The vectors come back in the order of the texts with the prompt of the purpose, also when later texts finish first.")]
    public async Task EmbedManyAsync_LaterTextsFinishFirst_ReturnsVectorsInTextOrderWithPurposePrompt() {
        string[] texts = ["first", "second", "third"];
        Mock<ILocalEmbeddingModel> model = new();
        model.Setup(m => m.Embed(It.IsAny<string>())).Returns((string prompt) => {
            int index = Array.FindIndex(texts, text => prompt.EndsWith(text));
            Thread.Sleep((texts.Length - index) * 100);
            float[] vector = new float[EmbeddingExpression.Dimensions];
            vector[0] = index + 1;
            return vector;
        });

        ISqlToken[] expressions = await ProducerOf(model.Object).EmbedManyAsync(texts, EmbeddingPurpose.Query, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(expressions, Has.Length.EqualTo(3));
            for (int i = 0; i < texts.Length; i++)
                Assert.That(((float[]) ExpressionPreparer.Prepare(expressions[i]).ConstantParameters[0])[0], Is.EqualTo(i + 1f), texts[i]);
            model.Verify(m => m.Embed(It.Is<string>(prompt => prompt.StartsWith("task: search result | query: "))), Times.Exactly(3));
        });
    }


    [Test, Parallelizable]
    [Description("A cancelled token stops the call before any text reaches the model.")]
    public void EmbedManyAsync_CancelledToken_ThrowsWithoutCallingModel() {
        Mock<ILocalEmbeddingModel> model = ModelAnswering(ModelVector);
        using CancellationTokenSource source = new();
        source.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(() => ProducerOf(model.Object).EmbedManyAsync(["a", "b"], EmbeddingPurpose.Query, source.Token));

        model.Verify(m => m.Embed(It.IsAny<string>()), Times.Never);
    }


    [Test, Parallelizable]
    [Description("The local producer names the vector space of EmbeddingGemma 2, which is what the model guard records.")]
    public void Model_Always_NamesEmbeddingGemma() {
        Assert.That(ProducerOf(Mock.Of<ILocalEmbeddingModel>()).Model, Is.EqualTo("embeddinggemma-2"));
    }


    [Test, Parallelizable]
    [Description("Preparing acquires the files before the model is loaded, and checks the loaded model with one query.")]
    public async Task PrepareAsync_Always_EnsuresFilesThenLoadsThenSelfChecks() {
        List<string> steps = [];
        Mock<IEmbeddingModelFiles> files = new();
        files.Setup(f => f.EnsureAsync(It.IsAny<CancellationToken>())).Callback(() => steps.Add("files")).Returns(Task.CompletedTask);
        Mock<ILocalEmbeddingModel> model = new();
        model.Setup(m => m.Load()).Callback(() => steps.Add("load"));
        model.Setup(m => m.Embed(It.IsAny<string>())).Callback<string>(prompt => steps.Add("embed:" + prompt.StartsWith("task: search result | query: "))).Returns(ModelVector);

        await ProducerOf(model.Object, files.Object).PrepareAsync(CancellationToken.None);

        Assert.That(steps, Is.EqualTo(new[] { "files", "load", "embed:True" }));
    }


    [Test, Parallelizable]
    [Description("The token the producer is prepared with is the token the file acquisition runs with.")]
    public async Task PrepareAsync_CancellationToken_ReachesFileAcquisition() {
        Mock<IEmbeddingModelFiles> files = new();
        using CancellationTokenSource source = new();

        await ProducerOf(ModelAnswering(ModelVector).Object, files.Object).PrepareAsync(source.Token);

        files.Verify(f => f.EnsureAsync(source.Token), Times.Once);
    }


    [Test, Parallelizable]
    [Description("Files which can not be provided stop the preparation before the model is touched.")]
    public void PrepareAsync_FilesFail_LeavesModelUntouched() {
        Mock<IEmbeddingModelFiles> files = new();
        files.Setup(f => f.EnsureAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("no files"));
        Mock<ILocalEmbeddingModel> model = new();

        InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(() => ProducerOf(model.Object, files.Object).PrepareAsync(CancellationToken.None));

        Assert.Multiple(() => {
            Assert.That(exception!.Message, Is.EqualTo("no files"));
            model.VerifyNoOtherCalls();
        });
    }


    [Test, Parallelizable]
    [Description("A model which fails its self check stops the preparation.")]
    public void PrepareAsync_SelfCheckFails_Throws() {
        Mock<ILocalEmbeddingModel> model = new();
        model.Setup(m => m.Embed(It.IsAny<string>())).Throws(new InvalidOperationException("wrong shape"));

        InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(() => ProducerOf(model.Object).PrepareAsync(CancellationToken.None));

        Assert.That(exception!.Message, Is.EqualTo("wrong shape"));
    }
}
