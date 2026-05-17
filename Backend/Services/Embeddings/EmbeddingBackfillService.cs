using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Microsoft.Extensions.Logging;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Expressions;
using Pooshit.Ocelot.Fields;
using Pooshit.Ocelot.Tokens;

namespace Backend.Services.Embeddings;

/// <summary>
/// one-shot service that walks all nodes with any embeddable surface (non-empty name OR
/// text content) and (re)generates their embedding using the v2 composition policy.
/// Postgres-only: if <see cref="IEmbeddingCapability.IsEnabled"/> is false the method
/// exits immediately with a log message and no database writes.
/// </summary>
public class EmbeddingBackfillService(IEntityManager database, IEmbeddingCapability embeddingCapability, ILogger<EmbeddingBackfillService> logger) {

    const int ProgressInterval = 25;

    readonly IEntityManager database = database;
    readonly IEmbeddingCapability embeddingCapability = embeddingCapability;
    readonly ILogger<EmbeddingBackfillService> logger = logger;

    /// <summary>
    /// builds the v2 candidate predicate: any row where the composition would yield a
    /// non-null embeddable string, i.e. name is non-empty OR (content is non-null AND
    /// content-type is text).
    /// </summary>
    static PredicateExpression<Node> CandidatePredicate() {
        PredicateExpression<Node> hasName = new PredicateExpression<Node>(n => n.Name != null && n.Name != "");

        PredicateExpression<Node> textTypePredicate = null;
        textTypePredicate |= n => n.ContentType.Like("text/%");
        textTypePredicate |= n => n.ContentType.In(TextContentTypePredicate.ApplicationTextTypes);

        PredicateExpression<Node> hasTextContent = null;
        hasTextContent &= n => n.Content != null;
        hasTextContent &= textTypePredicate;

        PredicateExpression<Node> predicate = null;
        predicate &= hasName | hasTextContent;
        return predicate;
    }

    /// <summary>
    /// runs the v2 backfill: re-embeds every node with any embeddable surface using
    /// the name+content composition policy.
    /// </summary>
    /// <param name="ct">cancellation token</param>
    public async Task RunAsync(CancellationToken ct = default) {
        if (!embeddingCapability.IsEnabled) {
            logger.LogInformation("event=backfill.skip reason=capability_disabled message=\"embedding capability is disabled; backfill is a no-op\"");
            return;
        }

        logger.LogInformation("event=backfill.start version=v2 composition=name_plus_content");
        DateTimeOffset started = DateTimeOffset.UtcNow;

        long total = await database.Load<Node>(DB.Count())
                                   .Where(CandidatePredicate().Content)
                                   .ExecuteScalarAsync<long>();

        logger.LogInformation("event=backfill.candidates total={Total}", total);

        int embedded = 0;
        int skipped = 0;

        await foreach (Node node in database.Load<Node>(n => n.Id, n => n.Name, n => n.ContentType, n => n.Content)
                                            .Where(CandidatePredicate().Content)
                                            .ExecuteEntitiesAsync()) {
            ct.ThrowIfCancellationRequested();

            string composed = EmbeddingInputComposer.Compose(node.Name, node.Content, node.ContentType);
            if (composed == null) {
                // composition returned null despite passing the candidate predicate — safety skip
                skipped++;
                continue;
            }

            await database.Update<Node>()
                          .Set(n => n.Embedding == DB.CustomFunction("embedding",
                                                                      DB.Constant(TextContentTypePredicate.EmbeddingModel),
                                                                      DB.Constant(composed)).Type<float[]>())
                          .Where(n => n.Id == node.Id)
                          .ExecuteAsync();

            embedded++;

            if (embedded % ProgressInterval == 0)
                logger.LogInformation("event=backfill.progress embedded={Embedded} of={Total}", embedded, total);
        }

        TimeSpan elapsed = DateTimeOffset.UtcNow - started;
        logger.LogInformation(
            "event=backfill.complete embedded={Embedded} skipped={Skipped} total={Total} elapsed={Elapsed}",
            embedded, skipped, total, elapsed);
    }
}
