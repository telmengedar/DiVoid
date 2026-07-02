using System;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.tests.Fixtures;
using Moq;
using Moq.Protected;
using NUnit.Framework;
using Pooshit.Http;

namespace Backend.tests.Tests;

/// <summary>
/// unit tests for <see cref="HttpEmbeddingProvider"/>.
///
/// uses a mock <see cref="HttpMessageHandler"/> to stub the HTTP endpoint — no real
/// network or embedding model is required.  write-path null-path tests (HP5) use
/// <see cref="DatabaseFixture"/> (SQLite).  HP6 (success path, HTTP invoked) requires
/// Postgres because <c>CastType.Vector</c> is not supported by SQLite; it skips via
/// <see cref="Assert.Inconclusive"/> when <c>POSTGRES_CONNECTION</c> is absent.
/// </summary>
[TestFixture]
public class HttpEmbeddingProviderTests
{
    const int TestDimension = EmbeddingCompositionPolicy.EmbeddingDimension;

    static float[] MakeVector(int dim = TestDimension)
        => Enumerable.Range(0, dim).Select(i => (float) i / dim).ToArray();

    static string BuildEmbeddingsJson(float[] vector) {
        string nums = string.Join(",", vector.Select(v => v.ToString("G", CultureInfo.InvariantCulture)));
        return $"{{\"data\":[{{\"embedding\":[{nums}]}}]}}";
    }

    static (HttpEmbeddingProvider provider, Mock<HttpMessageHandler> handlerMock) BuildProvider(
        HttpResponseMessage response) {

        Mock<HttpMessageHandler> handlerMock = new(MockBehavior.Strict);
        handlerMock.Protected()
                   .Setup<Task<HttpResponseMessage>>(
                       "SendAsync",
                       ItExpr.IsAny<HttpRequestMessage>(),
                       ItExpr.IsAny<CancellationToken>())
                   .ReturnsAsync(response);

        IHttpService httpService = new HttpService(handlerMock.Object);
        HttpEmbeddingProvider provider = new(
            httpService: httpService,
            endpoint: "http://stub-host/v1/embeddings",
            model: "test-model",
            apiKey: null,
            dimension: TestDimension,
            timeoutSeconds: 10
        );

        return (provider, handlerMock);
    }

    [Test]
    public void HP1_IsEnabled_True_DimensionMatchesConfig()
    {
        IHttpService stub = new HttpService(new HttpClientHandler());
        HttpEmbeddingProvider provider = new(stub, "http://x/v1/embeddings", "m", null, TestDimension, 10);
        Assert.Multiple(() => {
            Assert.That(provider.IsEnabled, Is.True, "HP1: HttpEmbeddingProvider.IsEnabled must be true");
            Assert.That(provider.Dimension, Is.EqualTo(TestDimension), "HP1: Dimension must match constructor arg");
        });
    }

    [Test]
    public async Task HP2_QueryVectorToken_SuccessResponse_ReturnsNonNullToken()
    {
        float[] vec = MakeVector();
        (HttpEmbeddingProvider provider, _) = BuildProvider(
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(BuildEmbeddingsJson(vec), Encoding.UTF8, "application/json")
            });

        Expression<Func<object, object>> token = await provider.QueryVectorTokenAsync("test query", CancellationToken.None);

        Assert.That(token, Is.Not.Null, "HP2: token must not be null for a successful response");
    }

    [Test]
    public void HP3_QueryVectorToken_HttpError_Throws()
    {
        (HttpEmbeddingProvider provider, _) = BuildProvider(
            new HttpResponseMessage(HttpStatusCode.InternalServerError) {
                Content = new StringContent("{\"error\":\"model not found\"}", Encoding.UTF8, "application/json")
            });

        Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.QueryVectorTokenAsync("test", CancellationToken.None),
            "HP3: HTTP 500 from endpoint must throw InvalidOperationException (fail-closed)");
    }

    [Test]
    public void HP4_QueryVectorToken_EmptyDataArray_Throws()
    {
        (HttpEmbeddingProvider provider, _) = BuildProvider(
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json")
            });

        Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.QueryVectorTokenAsync("test", CancellationToken.None),
            "HP4: empty data array must throw InvalidOperationException");
    }

    [Test]
    public async Task HP5_RegenerateEmbedding_NullComposition_SetsEmbeddingToNull()
    {
        Mock<HttpMessageHandler> handlerMock = new(MockBehavior.Strict);
        IHttpService httpService = new HttpService(handlerMock.Object);
        HttpEmbeddingProvider provider = new(
            httpService: httpService,
            endpoint: "http://stub/v1/embeddings",
            model: "test",
            apiKey: null,
            dimension: TestDimension,
            timeoutSeconds: 10
        );

        using DatabaseFixture fixture = new();
        long nodeId = await fixture.EntityManager.Insert<Node>()
                                                 .Columns(n => n.Name, n => n.TypeId)
                                                 .Values("", 0)
                                                 .ReturnID()
                                                 .ExecuteAsync();

        using Pooshit.Ocelot.Clients.Transaction transaction = fixture.EntityManager.Transaction();
        await provider.RegenerateEmbedding(fixture.EntityManager, transaction, nodeId, CancellationToken.None);
        transaction.Commit();

        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == nodeId)
                                              .ExecuteEntityAsync();

        Assert.That(raw.Embedding, Is.Null,
            "HP5: empty name + no content → null composition → Embedding must be set to null");
    }

    [Test]
    public async Task HP6_RegenerateEmbedding_NameOnlyNode_InvokesHttpEndpointOnce()
    {
        string? connString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION");
        if (string.IsNullOrEmpty(connString))
            Assert.Inconclusive("POSTGRES_CONNECTION not set — HP6 requires Postgres (CastType.Vector is unsupported by SQLite)");

        float[] expectedVector = MakeVector();
        (HttpEmbeddingProvider provider, Mock<HttpMessageHandler> handlerMock) = BuildProvider(
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(BuildEmbeddingsJson(expectedVector), Encoding.UTF8, "application/json")
            });

        using DatabaseFixture fixture = new();
        long nodeId = await fixture.EntityManager.Insert<Node>()
                                                 .Columns(n => n.Name, n => n.TypeId)
                                                 .Values("hp6-node", 0)
                                                 .ReturnID()
                                                 .ExecuteAsync();

        using Pooshit.Ocelot.Clients.Transaction tx = fixture.EntityManager.Transaction();
        await provider.RegenerateEmbedding(fixture.EntityManager, tx, nodeId, CancellationToken.None);
        tx.Commit();

        handlerMock.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }
}
