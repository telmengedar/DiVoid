using System;
using System.Threading.Tasks;
using Backend.Models;
using Backend.Models.Nodes;
using Microsoft.Extensions.Logging;
using Pooshit.Ocelot.Entities;

namespace Backend.Services.Embeddings;

/// <summary>
/// records the embedding model in <see cref="MetadataKeys.EmbeddingModel"/> and refuses a database holding vectors of another model
/// </summary>
public class EmbeddingModelGuard(IEntityManager database, IEmbeddingCapability embeddingCapability, ILogger<EmbeddingModelGuard> logger) : IEmbeddingModelGuard {

    /// <summary>
    /// model of every vector in a database that has vectors but no recorded model
    /// </summary>
    public const string UnrecordedVectorsModel = "gemini-embedding-001";

    /// <inheritdoc />
    public async Task EnsureAsync(string model) {
        if (!embeddingCapability.IsEnabled)
            return;

        string action = "matched";
        string recorded = await LoadRecorded();
        if (recorded == null) {
            if (model != UnrecordedVectorsModel && await HasVectors())
                throw Refusal($"unrecorded {UnrecordedVectorsModel} vectors", model);

            (recorded, bool inserted) = await Record(model);
            if (inserted)
                action = "inserted";
        }

        if (recorded != model)
            throw Refusal(recorded, model);

        logger.LogDebug("event=embedding.model action={Action} model={Model}", action, model);
    }

    async Task<string> LoadRecorded() {
        Metadata row = await database.Load<Metadata>()
                                     .Where(m => m.Name == MetadataKeys.EmbeddingModel)
                                     .ExecuteEntityAsync();
        return row?.Value;
    }

    async Task<bool> HasVectors() {
        long? id = await database.Load<Node>(n => n.Id)
                                 .Where(n => n.Embedding != null)
                                 .Limit(1)
                                 .ExecuteScalarAsync<long?>();
        return id != null;
    }

    async Task<(string Value, bool Inserted)> Record(string model) {
        try {
            await database.Insert<Metadata>()
                          .Columns(m => m.Name, m => m.Value)
                          .Values(MetadataKeys.EmbeddingModel, model)
                          .ExecuteAsync();
            return (model, true);
        } catch (Exception) {
            string winner = await LoadRecorded();
            if (winner == null)
                throw;

            return (winner, false);
        }
    }

    InvalidOperationException Refusal(string recorded, string model) {
        string message = $"embedding model mismatch: the database holds {recorded} but the configured producer writes {model}. configure the producer matching the database, or re-embed the database with the new producer (clear node.embedding, delete the '{MetadataKeys.EmbeddingModel}' row from metadata, run backfill-embeddings).";
        logger.LogError("{Message}", message);
        return new InvalidOperationException(message);
    }
}
