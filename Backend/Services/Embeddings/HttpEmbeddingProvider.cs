using System;
using System.Globalization;
using System.Linq.Expressions;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Pooshit.Http;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Fields;
using Pooshit.Ocelot.Tokens;
using Pooshit.Ocelot.Tokens.Values;

namespace Backend.Services.Embeddings;

/// <summary>
/// embedding provider that calls an OpenAI-compatible <c>/v1/embeddings</c> HTTP endpoint.
/// </summary>
public class HttpEmbeddingProvider : IEmbeddingProvider {

    readonly IHttpService httpService;
    readonly string endpoint;
    readonly string model;
    readonly string apiKey;

    /// <summary>
    /// creates a new <see cref="HttpEmbeddingProvider"/>.
    /// </summary>
    /// <param name="httpService">HTTP client used for embedding requests</param>
    /// <param name="endpoint">the <c>/v1/embeddings</c> URL</param>
    /// <param name="model">model identifier forwarded in every request</param>
    /// <param name="apiKey">bearer token (null/empty for keyless local servers like Ollama)</param>
    /// <param name="dimension">declared output dimension; fed to startup fail-closed gate</param>
    /// <param name="timeoutSeconds">per-call network timeout; defaults to 30 s when 0 or negative</param>
    public HttpEmbeddingProvider(IHttpService httpService, string endpoint, string model, string apiKey, int dimension, int timeoutSeconds) {
        this.httpService = httpService;
        this.endpoint = endpoint;
        this.model = model;
        this.apiKey = apiKey;
        Dimension = dimension;

        int effectiveTimeout = timeoutSeconds > 0 ? timeoutSeconds : 30;
        this.httpService.Timeout = TimeSpan.FromSeconds(effectiveTimeout);
    }

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public int Dimension { get; }

    /// <inheritdoc />
    public async Task RegenerateEmbedding(IEntityManager database, Transaction transaction, long nodeId, CancellationToken ct) {
        Node row = await database.Load<Node>(n => n.Name, n => n.Content, n => n.ContentType)
                                 .Where(n => n.Id == nodeId)
                                 .ExecuteEntityAsync(transaction);

        string composed = EmbeddingInputComposer.Compose(row.Name, row.Content, row.ContentType);

        if (composed == null) {
            await database.Update<Node>()
                          .Set(n => n.Embedding == (float[]) null)
                          .Where(n => n.Id == nodeId)
                          .ExecuteAsync(transaction);
            return;
        }

        ct.ThrowIfCancellationRequested();
        float[] vector = await CallEndpointAsync(composed);
        string capturedLiteral = FormatVectorLiteral(vector);

        await database.Update<Node>()
                      .Set(n => n.Embedding == DB.Cast(DB.Constant(capturedLiteral), CastType.Vector).Type<float[]>())
                      .Where(n => n.Id == nodeId)
                      .ExecuteAsync(transaction);
    }

    /// <inheritdoc />
    public async Task<Expression<Func<object, object>>> QueryVectorTokenAsync(string queryText, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        float[] vector = await CallEndpointAsync(queryText);
        string capturedLiteral = FormatVectorLiteral(vector);
        Expression<Func<object, object>> token = v => DB.Cast(DB.Constant(capturedLiteral), CastType.Vector);
        return token;
    }

    async Task<float[]> CallEndpointAsync(string input) {
        HttpEmbeddingsRequest request = new() {
            Input = input,
            Model = model,
            Dimensions = Dimension > 0 ? Dimension : (int?) null
        };

        HttpOptions options = new() {
            Headers = string.IsNullOrEmpty(apiKey)
                ? null
                : [new HttpHeader { Key = "Authorization", Value = $"Bearer {apiKey}" }]
        };

        HttpEmbeddingsResponse parsed;
        try {
            parsed = await httpService.Post<HttpEmbeddingsRequest, HttpEmbeddingsResponse>(endpoint, request, options);
        } catch (HttpServiceException ex) {
            string errorBody = await ex.Response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Embedding endpoint returned {(int) ex.Response.StatusCode}: {errorBody}", ex);
        }

        if (parsed?.Data == null || parsed.Data.Length == 0)
            throw new InvalidOperationException("Embedding endpoint returned empty data array.");

        return parsed.Data[0].Embedding
            ?? throw new InvalidOperationException("Embedding endpoint returned null embedding in first data item.");
    }

    static string FormatVectorLiteral(float[] vector) {
        StringBuilder sb = new(vector.Length * 10 + 2);
        sb.Append('[');
        for (int i = 0; i < vector.Length; i++) {
            if (i > 0)
                sb.Append(',');
            sb.Append(vector[i].ToString("G", CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        return sb.ToString();
    }
}
