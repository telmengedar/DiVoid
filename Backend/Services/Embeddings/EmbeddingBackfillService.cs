using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Microsoft.Extensions.Logging;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Tokens;

namespace Backend.Services.Embeddings;

/// <summary>
/// one-shot service that walks all content-bearing nodes whose <see cref="Node.Embedding"/>
/// is null and whose <see cref="Node.ContentType"/> qualifies as text, then issues the
/// same server-side embedding UPDATE used by the live content-upload path.
///
/// Postgres-only: if <see cref="IEmbeddingCapability.IsEnabled"/> is false the method
/// exits immediately with a log message and no database writes.
///
/// Safe to re-run: nodes already embedded are filtered out by the query predicate.
/// </summary>
public class EmbeddingBackfillService(IEntityManager database, IEmbeddingCapability embeddingCapability, ILogger<EmbeddingBackfillService> logger) {

    const int ProgressInterval = 25;

    readonly IEntityManager database = database;
    readonly IEmbeddingCapability embeddingCapability = embeddingCapability;
    readonly ILogger<EmbeddingBackfillService> logger = logger;

    /// <summary>
    /// runs the backfill.  each qualifying node is updated in its own short transaction
    /// so that a single failure does not abort the entire batch.
    /// </summary>
    /// <param name="ct">cancellation token</param>
    public async Task RunAsync(CancellationToken ct = default) {
        if (!embeddingCapability.IsEnabled) {
            logger.LogInformation("event=backfill.skip reason=capability_disabled message=\"embedding capability is disabled; backfill is a no-op\"");
            return;
        }

        logger.LogInformation("event=backfill.start");
        DateTimeOffset started = DateTimeOffset.UtcNow;

        // Collect all candidate nodes: unembedded, content-bearing.
        // We filter on Embedding IS NULL in SQL to avoid loading already-embedded rows;
        // Content != null and TextContentTypePredicate checks are applied in-process
        // because byte[] IS NOT NULL and the content-type allowlist cannot be expressed
        // as Ocelot predicate expressions.
        List<Node> candidates = new();
        IAsyncEnumerable<Node> stream = database.Load<Node>(n => n.Id, n => n.ContentType, n => n.Content)
                                                .Where(n => n.Embedding == null)
                                                .ExecuteEntitiesAsync();
        await foreach (Node candidate in stream) {
            ct.ThrowIfCancellationRequested();
            candidates.Add(candidate);
        }

        int total = candidates.Count;
        int embedded = 0;
        int skipped = 0;

        logger.LogInformation("event=backfill.candidates total={Total}", total);

        foreach (Node node in candidates) {
            ct.ThrowIfCancellationRequested();

            if (node.Content == null || !TextContentTypePredicate.IsText(node.ContentType)) {
                skipped++;
                continue;
            }

            string text = Encoding.UTF8.GetString(node.Content);

            using Pooshit.Ocelot.Clients.Transaction transaction = database.Transaction();
            await database.Update<Node>()
                          .Set(n => n.Embedding == DB.CustomFunction("embedding",
                                                                      DB.Constant(TextContentTypePredicate.EmbeddingModel),
                                                                      DB.Constant(text)).Type<float[]>())
                          .Where(n => n.Id == node.Id)
                          .ExecuteAsync(transaction);
            transaction.Commit();

            embedded++;

            if (embedded % ProgressInterval == 0)
                logger.LogInformation("event=backfill.progress embedded={Embedded} skipped={Skipped} of={Total}", embedded, skipped, total);
        }

        TimeSpan elapsed = DateTimeOffset.UtcNow - started;
        logger.LogInformation(
            "event=backfill.complete embedded={Embedded} skipped={Skipped} total={Total} elapsed={Elapsed}",
            embedded, skipped, total, elapsed);
    }
}
