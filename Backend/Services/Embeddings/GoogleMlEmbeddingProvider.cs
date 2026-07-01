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
using Pooshit.Ocelot.Tokens.Values;

namespace Backend.Services.Embeddings;

/// <summary>
/// embedding provider that delegates entirely to the Postgres
/// <c>google_ml_integration</c> <c>embedding(model, text)</c> function.
///
/// write path: runs the four WHERE-predicated server-side UPDATEs (F1–F4) that
/// compose and embed entirely inside Postgres — no content blob is transferred to .NET
/// (PR #81 preserved, per Option D §4.4 of the embedding-providers design doc).
///
/// query path: emits an inline <c>embedding(model, text)</c> function-call token,
/// identical SQL to the pre-refactor NodeMapper.
///
/// composition policy constants are read from <see cref="EmbeddingCompositionPolicy"/>
/// so that the SQL truncation budget stays aligned with the C# composer.
/// </summary>
public class GoogleMlEmbeddingProvider : IEmbeddingProvider {

    readonly string model;

    /// <summary>
    /// creates a new <see cref="GoogleMlEmbeddingProvider"/>.
    /// </summary>
    /// <param name="model">
    /// embedding model identifier (e.g. <c>"gemini-embedding-001"</c>).
    /// used in every SQL <c>embedding(model, text)</c> call.
    /// </param>
    /// <param name="dimension">
    /// declared output dimension.  must equal the <c>[Size(...)]</c> attribute on
    /// <c>Node.Embedding</c> or the startup fail-closed gate fires.
    /// </param>
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
        (UpdateValuesOperation<Node> f1, UpdateValuesOperation<Node> f2,
         UpdateValuesOperation<Node> f3, UpdateValuesOperation<Node> f4) =
            BuildEmbeddingBranchOperations(database, nodeId, model);

        await f1.ExecuteAsync(transaction);
        ct.ThrowIfCancellationRequested();

        await f2.ExecuteAsync(transaction);
        ct.ThrowIfCancellationRequested();

        await f3.ExecuteAsync(transaction);
        ct.ThrowIfCancellationRequested();

        await f4.ExecuteAsync(transaction);
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
    /// constructs the four UPDATE operation trees for server-side embedding regeneration.
    ///
    /// single source of truth called by <see cref="RegenerateEmbedding"/> (production
    /// execution) and by <c>EmbeddingPatchSqlShapeTests.RenderAllBranches</c>
    /// (SQL-shape assertions).  changes here propagate to both automatically.
    ///
    /// branches:
    ///   F1 — name + text content →
    ///        embed(name ++ sep ++ LEFT(convert_from(content,'UTF8'), MaxLength − sep_len))
    ///        note: budget is the constant (MaxLength − sep.Length), not dynamic per-name.
    ///        see inline comment for the accepted divergence vs the C# composer.
    ///   F2 — name only, non-text or no content → embed(LEFT(name, MaxLength))
    ///   F3 — content only, empty/null name, text content →
    ///        embed(LEFT(convert_from(content,'UTF8'), MaxLength))
    ///   F4 — neither name nor text content → NULL
    ///
    /// the F1 content budget uses a constant (MaxLength − sep.Length).  a very long
    /// name may make the composed string slightly exceed MaxLength.  Ocelot's
    /// expression-tree API does not support SQL arithmetic as DB.CustomFunction
    /// arguments, so GREATEST(0, budget − CHAR_LENGTH(name)) cannot be expressed
    /// here; this is an accepted divergence from the C# composer (see §6.7).
    /// </summary>
    internal static (UpdateValuesOperation<Node> F1,
                     UpdateValuesOperation<Node> F2,
                     UpdateValuesOperation<Node> F3,
                     UpdateValuesOperation<Node> F4)
        BuildEmbeddingBranchOperations(IEntityManager database, long nodeId, string model) {

        string sep = EmbeddingCompositionPolicy.Separator;
        int maxLen = EmbeddingCompositionPolicy.MaxLength;
        int maxLenMinusSep = maxLen - sep.Length;
        string[] allowlist = EmbeddingCompositionPolicy.ApplicationTextTypes;

        // F1 content budget: use maxLenMinusSep as a constant.
        // This reserves exactly (MaxLength - sep.Length) characters for content, regardless of name length.
        // The C# composer uses Math.Max(0, MaxLength - name.Length - sep.Length) (dynamic, per-name budget).
        // Accepted divergence: when a name is very long, the SQL form may produce a composed string
        // slightly over MaxLength. For realistic names (< a few hundred chars) the results are identical.
        // Ocelot's expression-tree API does not support SQL arithmetic as DB.CustomFunction arguments
        // (DB.Constant(n).Int64 returns a plain long, not an ISqlToken), so GREATEST(0, n - CHAR_LENGTH(x))
        // cannot be expressed in this context. Noted in design doc §6.7 accepted-divergence table.
        UpdateValuesOperation<Node> f1 = database.Update<Node>()
            .Set(n => n.Embedding == DB.CustomFunction("embedding",
                         DB.Constant(model),
                         DB.CustomFunction("concat",
                             DB.Property<Node>(x => x.Name),
                             DB.Constant(sep),
                             DB.Left(DB.ConvertFrom(DB.Property<Node>(x => x.Content), "UTF8"),
                                     maxLenMinusSep)
                         )).Type<float[]>())
            .Where(n => n.Id == nodeId
                     && n.Name != null && n.Name != ""
                     && (n.ContentType.Like("text/%") || n.ContentType.In(allowlist))
                     && n.Content != null);

        UpdateValuesOperation<Node> f2 = database.Update<Node>()
            .Set(n => n.Embedding == DB.CustomFunction("embedding",
                         DB.Constant(model),
                         DB.Left(DB.Property<Node>(x => x.Name), maxLen)).Type<float[]>())
            .Where(n => n.Id == nodeId
                     && n.Name != null && n.Name != ""
                     && (!(n.ContentType.Like("text/%") || n.ContentType.In(allowlist)) || n.Content == null));

        UpdateValuesOperation<Node> f3 = database.Update<Node>()
            .Set(n => n.Embedding == DB.CustomFunction("embedding",
                         DB.Constant(model),
                         DB.Left(DB.ConvertFrom(DB.Property<Node>(x => x.Content), "UTF8"), maxLen)).Type<float[]>())
            .Where(n => n.Id == nodeId
                     && (n.Name == null || n.Name == "")
                     && (n.ContentType.Like("text/%") || n.ContentType.In(allowlist))
                     && n.Content != null);

        UpdateValuesOperation<Node> f4 = database.Update<Node>()
            .Set(n => n.Embedding == (float[]) null)
            .Where(n => n.Id == nodeId
                     && (n.Name == null || n.Name == "")
                     && (!(n.ContentType.Like("text/%") || n.ContentType.In(allowlist)) || n.Content == null));

        return (f1, f2, f3, f4);
    }
}
