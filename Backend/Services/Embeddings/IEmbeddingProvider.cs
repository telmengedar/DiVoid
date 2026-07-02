using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;

namespace Backend.Services.Embeddings;

/// <summary>
/// operation-level seam for pluggable embedding generation (write path and query path).
/// </summary>
public interface IEmbeddingProvider {

    /// <summary>
    /// sets the node's <c>Embedding</c> column inside <paramref name="transaction"/>.
    /// on failure, throws so the caller's transaction rolls back (fail-closed).
    /// </summary>
    Task RegenerateEmbedding(IEntityManager database, Transaction transaction, long nodeId, CancellationToken ct);

    /// <summary>
    /// returns an Ocelot SQL expression token whose evaluated value is the embedding of <paramref name="queryText"/>,
    /// usable as the left operand of a <c>VCos</c> cosine comparison.
    /// </summary>
    Task<Expression<Func<object, object>>> QueryVectorTokenAsync(string queryText, CancellationToken ct);

    /// <summary>
    /// the integer output dimension this provider produces.
    /// </summary>
    int Dimension { get; }

    /// <summary>
    /// true when a real embedding provider is configured; false for <see cref="NullEmbeddingProvider"/>.
    /// </summary>
    bool IsEnabled { get; }
}
