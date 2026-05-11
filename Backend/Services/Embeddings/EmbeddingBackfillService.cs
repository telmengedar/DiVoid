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
    /// builds the SQL predicate that selects candidate nodes: unembedded, non-null
    /// content, and a content-type that qualifies as embeddable text.
    /// text/* OR the application/* allowlist from <see cref="TextContentTypePredicate.ApplicationTextTypes"/>.
    /// </summary>
    static PredicateExpression<Node> CandidatePredicate() {
        PredicateExpression<Node> typePredicate = null;
        typePredicate |= n => n.ContentType.Like("text/%");
        typePredicate |= n => n.ContentType.In(TextContentTypePredicate.ApplicationTextTypes);

        PredicateExpression<Node> predicate = null;
        predicate &= n => n.Embedding == null;
        predicate &= n => n.Content != null;
        predicate &= typePredicate;
        return predicate;
    }

    /// <summary>
    /// runs the backfill.
    /// </summary>
    /// <param name="ct">cancellation token</param>
    public async Task RunAsync(CancellationToken ct = default) {
        if (!embeddingCapability.IsEnabled) {
            logger.LogInformation("event=backfill.skip reason=capability_disabled message=\"embedding capability is disabled; backfill is a no-op\"");
            return;
        }

        logger.LogInformation("event=backfill.start");
        DateTimeOffset started = DateTimeOffset.UtcNow;

        long total = await database.Load<Node>(DB.Count())
                                   .Where(CandidatePredicate().Content)
                                   .ExecuteScalarAsync<long>();

        logger.LogInformation("event=backfill.candidates total={Total}", total);

        int embedded = 0;

        await foreach (Node node in database.Load<Node>(n => n.Id, n => n.ContentType, n => n.Content)
                                            .Where(CandidatePredicate().Content)
                                            .ExecuteEntitiesAsync()) {
            ct.ThrowIfCancellationRequested();

            string text = Encoding.UTF8.GetString(node.Content);

            await database.Update<Node>()
                          .Set(n => n.Embedding == DB.CustomFunction("embedding",
                                                                      DB.Constant(TextContentTypePredicate.EmbeddingModel),
                                                                      DB.Constant(text)).Type<float[]>())
                          .Where(n => n.Id == node.Id)
                          .ExecuteAsync();

            embedded++;

            if (embedded % ProgressInterval == 0)
                logger.LogInformation("event=backfill.progress embedded={Embedded} of={Total}", embedded, total);
        }

        TimeSpan elapsed = DateTimeOffset.UtcNow - started;
        logger.LogInformation(
            "event=backfill.complete embedded={Embedded} total={Total} elapsed={Elapsed}",
            embedded, total, elapsed);
    }
}
