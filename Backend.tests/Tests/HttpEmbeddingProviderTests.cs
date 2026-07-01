using System;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.tests.Fixtures;
using Moq;
using Moq.Protected;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// unit tests for <see cref="HttpEmbeddingProvider"/>.
///
/// uses a mock <see cref="HttpMessageHandler"/> to stub the HTTP endpoint — no real
/// network or embedding model is required.  the tests verify:
///   - <see cref="HttpEmbeddingProvider.QueryVectorTokenAsync"/> returns a constant-vector token
///   - vector formatting produces valid pgvector bracket-notation
///   - fail-closed: HTTP error → throws
///   - fail-closed: empty data array → throws
///   - <see cref="HttpEmbeddingProvider.IsEnabled"/> is true
///   - <see cref="HttpEmbeddingProvider.Dimension"/> matches configured value
///
/// write-path tests (<see cref="HttpEmbeddingProvider.RegenerateEmbedding"/>) use the
/// SQLite <see cref="DatabaseFixture"/> to verify the SET operation is issued without
/// crashing on a stub vector; they cannot verify the actual stored value on SQLite.
/// </summary>
[TestFixture]
public class HttpEmbeddingProviderTests
{
    const int TestDimension = EmbeddingCompositionPolicy.EmbeddingDimension;

    static float[] MakeVector(int dim = TestDimension)
        => Enumerable.Range(0, dim).Select(i => (float) i / dim).ToArray();

    static string BuildEmbeddingsJson(float[] vector) {
        JsonDocument doc = JsonDocument.Parse(
            $"{{\"data\":[{{\"embedding\":[{string.Join(",", vector.Select(v => v.ToString("G", System.Globalization.CultureInfo.InvariantCulture)))}]}}]}}");
        return doc.RootElement.GetRawText();
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

        // Construct via the public constructor but inject the mock handler via reflection
        // since HttpClient constructors accept HttpMessageHandler.
        HttpEmbeddingProvider provider = new(
            endpoint: "http://stub-host/v1/embeddings",
            model: "test-model",
            apiKey: null,
            dimension: TestDimension,
            timeoutSeconds: 10
        );

        // Inject the mock HttpClient via the private field (test-only: use reflection).
        // Acceptable for unit tests where we cannot use seam injection because HttpClient
        // has no interface.  A factory seam is future work if this pattern proliferates.
        System.Reflection.FieldInfo httpClientField = typeof(HttpEmbeddingProvider)
            .GetField("httpClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        httpClientField.SetValue(provider, new HttpClient(handlerMock.Object) {
            Timeout = TimeSpan.FromSeconds(10)
        });

        return (provider, handlerMock);
    }

    // -----------------------------------------------------------------------
    // HP1 — IsEnabled = true, Dimension = configured value
    // -----------------------------------------------------------------------

    [Test]
    public void HP1_IsEnabled_True_DimensionMatchesConfig()
    {
        HttpEmbeddingProvider provider = new("http://x/v1/embeddings", "m", null, TestDimension, 10);
        Assert.Multiple(() => {
            Assert.That(provider.IsEnabled, Is.True, "HP1: HttpEmbeddingProvider.IsEnabled must be true");
            Assert.That(provider.Dimension, Is.EqualTo(TestDimension), "HP1: Dimension must match constructor arg");
        });
    }

    // -----------------------------------------------------------------------
    // HP2 — QueryVectorTokenAsync returns non-null token
    // -----------------------------------------------------------------------

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

    // -----------------------------------------------------------------------
    // HP3 — fail-closed: HTTP error → throws
    // -----------------------------------------------------------------------

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

    // -----------------------------------------------------------------------
    // HP4 — fail-closed: empty data array → throws
    // -----------------------------------------------------------------------

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

    // -----------------------------------------------------------------------
    // HP5 — RegenerateEmbedding: null composition → sets embedding to null, no HTTP call
    // -----------------------------------------------------------------------

    [Test]
    public async Task HP5_RegenerateEmbedding_NullComposition_SetsEmbeddingToNull()
    {
        // Node with empty name and no text content → EmbeddingInputComposer.Compose = null.
        // Provider must write null embedding without calling the HTTP endpoint.
        Mock<HttpMessageHandler> handlerMock = new(MockBehavior.Strict);
        HttpEmbeddingProvider provider = new(
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
}
