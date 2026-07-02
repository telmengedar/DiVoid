using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;

namespace Backend.Services.Embeddings;

/// <summary>
/// no-op embedding provider used when no provider is configured or when running on SQLite.
/// </summary>
public class NullEmbeddingProvider : IEmbeddingProvider {

    /// <summary>
    /// shared singleton — the null provider carries no state.
    /// </summary>
    public static readonly NullEmbeddingProvider Instance = new();

    /// <inheritdoc />
    public bool IsEnabled => false;

    /// <inheritdoc />
    public int Dimension => 0;

    /// <inheritdoc />
    public Task RegenerateEmbedding(IEntityManager database, Transaction transaction, long nodeId, CancellationToken ct)
        => throw new InvalidOperationException("NullEmbeddingProvider.RegenerateEmbedding must never be called — caller must check IsEnabled first.");

    /// <inheritdoc />
    public Task<Expression<Func<object, object>>> QueryVectorTokenAsync(string queryText, CancellationToken ct)
        => throw new InvalidOperationException("NullEmbeddingProvider.QueryVectorTokenAsync must never be called — caller must check IsEnabled first.");
}
