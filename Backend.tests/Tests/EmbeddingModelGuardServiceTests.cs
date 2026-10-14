using Backend.Init;
using Backend.Services.Embeddings;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// pins the order of the embedding startup step: the model guard first, then the preparation of the producer
/// </summary>
[TestFixture, Parallelizable]
public class EmbeddingModelGuardServiceTests {

    static (EmbeddingModelGuardService Service, Mock<IEmbeddingModelGuard> Guard, Mock<IEmbeddingProducer> Producer, List<string> Order) Create(bool enabled) {
        List<string> order = [];
        Mock<IEmbeddingModelGuard> guard = new();
        guard.Setup(g => g.EnsureAsync(It.IsAny<string>())).Callback(() => order.Add("guard")).Returns(Task.CompletedTask);
        Mock<IEmbeddingProducer> producer = new();
        producer.SetupGet(p => p.Model).Returns("some-model");
        producer.Setup(p => p.PrepareAsync(It.IsAny<CancellationToken>())).Callback(() => order.Add("prepare")).Returns(Task.CompletedTask);
        EmbeddingModelGuardService service = new(guard.Object, producer.Object, new EmbeddingCapability(enabled), NullLogger<EmbeddingModelGuardService>.Instance);
        return (service, guard, producer, order);
    }


    [Test, Parallelizable]
    [Description("The guard runs with the model of the producer, and the producer is prepared only after it passed.")]
    public async Task StartAsync_Enabled_EnsuresGuardThenPreparesProducer() {
        (EmbeddingModelGuardService service, Mock<IEmbeddingModelGuard> guard, _, List<string> order) = Create(true);

        await service.StartAsync(CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(order, Is.EqualTo(new[] { "guard", "prepare" }));
            guard.Verify(g => g.EnsureAsync("some-model"), Times.Once);
        });
    }


    [Test, Parallelizable]
    [Description("A database the guard refuses never causes a model download, because the producer is not prepared.")]
    public void StartAsync_GuardRefuses_DoesNotPrepareProducer() {
        (EmbeddingModelGuardService service, Mock<IEmbeddingModelGuard> guard, Mock<IEmbeddingProducer> producer, _) = Create(true);
        guard.Setup(g => g.EnsureAsync(It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("mismatch"));

        Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));

        producer.Verify(p => p.PrepareAsync(It.IsAny<CancellationToken>()), Times.Never);
    }


    [Test, Parallelizable]
    [Description("A producer which can not be prepared refuses startup.")]
    public void StartAsync_PreparationFails_Throws() {
        (EmbeddingModelGuardService service, _, Mock<IEmbeddingProducer> producer, _) = Create(true);
        producer.Setup(p => p.PrepareAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("no model"));

        InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));

        Assert.That(exception!.Message, Is.EqualTo("no model"));
    }


    [Test, Parallelizable]
    [Description("The cancellation token of the host reaches the preparation, so a stopping host can end a download.")]
    public async Task StartAsync_Enabled_PassesCancellationTokenToPreparation() {
        (EmbeddingModelGuardService service, _, Mock<IEmbeddingProducer> producer, _) = Create(true);
        using CancellationTokenSource source = new();

        await service.StartAsync(source.Token);

        producer.Verify(p => p.PrepareAsync(source.Token), Times.Once);
    }


    [Test, Parallelizable]
    [Description("Without the embedding capability the step does nothing.")]
    public async Task StartAsync_Disabled_TouchesNeitherGuardNorProducer() {
        (EmbeddingModelGuardService service, Mock<IEmbeddingModelGuard> guard, Mock<IEmbeddingProducer> producer, _) = Create(false);

        await service.StartAsync(CancellationToken.None);

        Assert.Multiple(() => {
            guard.VerifyNoOtherCalls();
            producer.VerifyNoOtherCalls();
        });
    }
}
