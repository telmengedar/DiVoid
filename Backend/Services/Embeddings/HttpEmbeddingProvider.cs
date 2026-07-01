using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Fields;
using Pooshit.Ocelot.Tokens;
using Pooshit.Ocelot.Tokens.Values;

namespace Backend.Services.Embeddings;

/// <summary>
/// embedding provider that calls an OpenAI-compatible <c>/v1/embeddings</c> HTTP endpoint
/// (OpenAI, Ollama, LM Studio, Hugging Face text-embeddings-inference — shared wire shape).
///
/// write path: reads <c>(name, content, contentType)</c> for the node in-transaction,
/// composes via <see cref="EmbeddingInputComposer"/>, posts to the configured endpoint under
/// a bounded timeout, then writes the returned vector as a constant pgvector literal.
/// on any failure (timeout, HTTP error, parse error) throws so the caller's transaction
/// rolls back (fail-closed per §9 of the design doc).
///
/// query path: posts the query text to the endpoint, returns a constant-vector Ocelot
/// expression token for use in the cosine similarity ranking query.
/// </summary>
public class HttpEmbeddingProvider : IEmbeddingProvider {

    readonly HttpClient httpClient;
    readonly string endpoint;
    readonly string model;
    readonly string apiKey;

    /// <summary>
    /// creates a new <see cref="HttpEmbeddingProvider"/>.
    /// </summary>
    /// <param name="endpoint">the <c>/v1/embeddings</c> URL</param>
    /// <param name="model">model identifier forwarded in every request</param>
    /// <param name="apiKey">bearer token (null/empty for keyless local servers like Ollama)</param>
    /// <param name="dimension">declared output dimension; fed to startup fail-closed gate</param>
    /// <param name="timeoutSeconds">
    /// per-call network timeout; defaults to 30 s when 0 or negative.
    /// applies to both <see cref="RegenerateEmbedding"/> and <see cref="QueryVectorTokenAsync"/>.
    /// </param>
    public HttpEmbeddingProvider(string endpoint, string model, string apiKey, int dimension, int timeoutSeconds) {
        this.endpoint = endpoint;
        this.model = model;
        this.apiKey = apiKey;
        Dimension = dimension;

        int effectiveTimeout = timeoutSeconds > 0 ? timeoutSeconds : 30;
        httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(effectiveTimeout) };
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

        float[] vector = await CallEndpointAsync(composed, ct);
        string capturedLiteral = FormatVectorLiteral(vector);

        await database.Update<Node>()
                      .Set(n => n.Embedding == DB.Cast(DB.Constant(capturedLiteral), CastType.Vector).Type<float[]>())
                      .Where(n => n.Id == nodeId)
                      .ExecuteAsync(transaction);
    }

    /// <inheritdoc />
    public async Task<Expression<Func<object, object>>> QueryVectorTokenAsync(string queryText, CancellationToken ct) {
        float[] vector = await CallEndpointAsync(queryText, ct);
        string capturedLiteral = FormatVectorLiteral(vector);
        Expression<Func<object, object>> token = v => DB.Cast(DB.Constant(capturedLiteral), CastType.Vector);
        return token;
    }

    async Task<float[]> CallEndpointAsync(string input, CancellationToken ct) {
        HttpEmbeddingsRequest request = new() {
            Input = input,
            Model = model,
            Dimensions = Dimension > 0 ? Dimension : null
        };

        string body = JsonSerializer.Serialize(request, HttpProviderJsonContext.Default.HttpEmbeddingsRequest);

        using HttpRequestMessage message = new(HttpMethod.Post, endpoint) {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrEmpty(apiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response = await httpClient.SendAsync(message, ct);

        if (!response.IsSuccessStatusCode) {
            string errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Embedding endpoint returned {(int)response.StatusCode}: {errorBody}");
        }

        string responseJson = await response.Content.ReadAsStringAsync(ct);
        HttpEmbeddingsResponse parsed = JsonSerializer.Deserialize(responseJson, HttpProviderJsonContext.Default.HttpEmbeddingsResponse);

        if (parsed?.Data == null || parsed.Data.Count == 0)
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

sealed class HttpEmbeddingsRequest {
    [JsonPropertyName("input")]
    public string Input { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; }

    [JsonPropertyName("dimensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Dimensions { get; set; }
}

sealed class HttpEmbeddingData {
    [JsonPropertyName("embedding")]
    public float[] Embedding { get; set; }
}

sealed class HttpEmbeddingsResponse {
    [JsonPropertyName("data")]
    public List<HttpEmbeddingData> Data { get; set; }
}

[JsonSerializable(typeof(HttpEmbeddingsRequest))]
[JsonSerializable(typeof(HttpEmbeddingsResponse))]
[JsonSerializable(typeof(HttpEmbeddingData))]
[JsonSerializable(typeof(List<HttpEmbeddingData>))]
sealed partial class HttpProviderJsonContext : JsonSerializerContext { }
