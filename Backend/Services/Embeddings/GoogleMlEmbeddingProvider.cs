using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Fields;
using Pooshit.Ocelot.Tokens;
using Pooshit.Ocelot.Tokens.Control;
using Pooshit.Ocelot.Tokens.Values;

namespace Backend.Services.Embeddings;

/// <summary>
/// embedding provider backed by the Postgres <c>google_ml_integration</c> <c>embedding(model, text)</c> function.
/// </summary>
public class GoogleMlEmbeddingProvider : IEmbeddingProvider {

    readonly string model;

    /// <summary>
    /// creates a new <see cref="GoogleMlEmbeddingProvider"/>.
    /// </summary>
    /// <param name="model">embedding model identifier (e.g. <c>"gemini-embedding-001"</c>)</param>
    /// <param name="dimension">declared output dimension; must equal <see cref="EmbeddingCompositionPolicy.EmbeddingDimension"/></param>
    public GoogleMlEmbeddingProvider(string model, int dimension = EmbeddingCompositionPolicy.EmbeddingDimension) {
        this.model = model;
        Dimension = dimension;
    }

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public int Dimension { get; }

    /// <inheritdoc />
    public async Task RegenerateEmbedding(IEntityManager database, Transaction transaction, long nodeId, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        await BuildEmbeddingUpdate(database, nodeId, model).ExecuteAsync(transaction);
    }

    /// <inheritdoc />
    public Task<Expression<Func<object, object>>> QueryVectorTokenAsync(string queryText, CancellationToken ct) {
        string capturedModel = model;
        Expression<Func<object, object>> token =
            v => DB.Cast(DB.CustomFunction("embedding",
                             DB.Constant(capturedModel),
                             DB.Constant(queryText)), CastType.Vector);
        return Task.FromResult(token);
    }

    /// <summary>
    /// builds a single UPDATE that sets <c>Embedding</c> via a server-side CASE expression.
    /// shared by <see cref="RegenerateEmbedding"/> (production) and SQL-shape tests.
    /// </summary>
    internal static UpdateValuesOperation<Node> BuildEmbeddingUpdate(IEntityManager database, long nodeId, string model) {
        string sep = EmbeddingCompositionPolicy.Separator;
        int maxLen = EmbeddingCompositionPolicy.MaxLength;
        int maxLenMinusSep = maxLen - sep.Length;
        string[] allowlist = EmbeddingCompositionPolicy.ApplicationTextTypes;

        When w1 = DB.When(
            DB.Predicate<Node>(n => n.Name != null && n.Name != ""
                && (n.ContentType.Like("text/%") || n.ContentType.In(allowlist))
                && n.Content != null),
            DB.CustomFunction("embedding",
                DB.Constant(model),
                DB.CustomFunction("concat",
                    DB.Property<Node>(x => x.Name),
                    DB.Constant(sep),
                    DB.Left(DB.ConvertFrom(DB.Property<Node>(x => x.Content), "UTF8"),
                            maxLenMinusSep))));

        When w2 = DB.When(
            DB.Predicate<Node>(n => n.Name != null && n.Name != ""
                && (!(n.ContentType.Like("text/%") || n.ContentType.In(allowlist)) || n.Content == null)),
            DB.CustomFunction("embedding",
                DB.Constant(model),
                DB.Left(DB.Property<Node>(x => x.Name), maxLen)));

        When w3 = DB.When(
            DB.Predicate<Node>(n => (n.Name == null || n.Name == "")
                && (n.ContentType.Like("text/%") || n.ContentType.In(allowlist))
                && n.Content != null),
            DB.CustomFunction("embedding",
                DB.Constant(model),
                DB.Left(DB.ConvertFrom(DB.Property<Node>(x => x.Content), "UTF8"), maxLen)));

        return database.Update<Node>()
            .Set(n => n.Embedding == DB.Case(new When[] { w1, w2, w3 }, DB.Constant((float[]) null)).Type<float[]>())
            .Where(n => n.Id == nodeId);
    }
}
