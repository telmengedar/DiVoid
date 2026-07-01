using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;

namespace Backend.Services.Embeddings;

/// <summary>
/// operation-level seam for pluggable embedding generation.
///
/// two altitudes (§4.2 of the embedding-providers design doc):
///   - write path: the provider executes the embed operation for a node in a transaction.
///   - query path: the provider provides an Ocelot SQL token for the query vector.
///
/// implementations: <see cref="GoogleMlEmbeddingProvider"/> (default, server-side SQL),
/// <see cref="HttpEmbeddingProvider"/> (app-side HTTP, OpenAI-compatible),
/// <see cref="NullEmbeddingProvider"/> (no-op when no provider is configured / SQLite).
/// </summary>
public interface IEmbeddingProvider {

    /// <summary>
    /// executes the embedding write for node <paramref name="nodeId"/> inside
    /// <paramref name="transaction"/>.
    ///
    /// precondition: the field write (name/content) for <paramref name="nodeId"/>
    /// is already applied in <paramref name="transaction"/>; caller has verified
    /// <see cref="IsEnabled"/> is true before calling.
    ///
    /// effect: sets the node's <c>Embedding</c> column to the embedding of its
    /// current persisted <c>(name, content, contentType)</c>, or to null when
    /// there is no embeddable surface.  idempotent — re-running produces the same
    /// embedding for the same row state.
    ///
    /// on failure: throws so the caller's transaction rolls back (fail-closed).
    /// never swallows exceptions.
    /// </summary>
    Task RegenerateEmbedding(IEntityManager database, Transaction transaction, long nodeId, CancellationToken ct);

    /// <summary>
    /// returns an Ocelot SQL expression token whose evaluated value is the
    /// <see cref="Dimension"/>-float embedding of <paramref name="queryText"/>,
    /// usable as the left operand of a <c>VCos</c> cosine comparison.
    ///
    /// for google: a server-side inline <c>embedding(model, text)</c> function call —
    /// no side effects, SQL-only.
    /// for http: performs exactly one network call to obtain the query vector and
    /// returns a constant-vector token; on failure throws (fail-closed).
    /// </summary>
    Task<Expression<Func<object, object>>> QueryVectorTokenAsync(string queryText, CancellationToken ct);

    /// <summary>
    /// the integer output dimension this provider produces.
    /// must equal the <c>[Size(...)]</c> attribute on <c>Node.Embedding</c>
    /// or the service refuses to start (dimension fail-closed gate in Startup).
    /// </summary>
    int Dimension { get; }

    /// <summary>
    /// true when a real embedding provider is configured.
    /// false only for <see cref="NullEmbeddingProvider"/>.
    /// every write path and search guard checks this before doing embedding work.
    /// subsumes <c>IEmbeddingCapability.IsEnabled</c>.
    /// </summary>
    bool IsEnabled { get; }
}
