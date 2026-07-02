using System;
using System.Threading;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Microsoft.Extensions.Logging;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Expressions;
using Pooshit.Ocelot.Fields;
using Pooshit.Ocelot.Tokens;

namespace Backend.Services.Embeddings;

/// <summary>
/// one-shot service that walks all nodes with any embeddable surface (non-empty name OR
/// text content) and (re)generates their embedding using the configured provider.
/// exits immediately when <see cref="IEmbeddingProvider.IsEnabled"/> is false.
///
/// each row is processed in its own transaction so a single-row failure does not
/// roll back the whole run — the provider is responsible for throwing on error
/// (fail-closed per §9 of the design doc).
/// </summary>
public class EmbeddingBackfillService(IEntityManager database, IEmbeddingProvider embeddingProvider, ILogger<EmbeddingBackfillService> logger) {

    const int ProgressInterval = 25;

    readonly IEntityManager database = database;
    readonly IEmbeddingProvider embeddingProvider = embeddingProvider;
    readonly ILogger<EmbeddingBackfillService> logger = logger;

    /// <summary>
    /// builds the v2 candidate predicate: any row where the composition would yield a
    /// non-null embeddable string, i.e. name is non-empty OR (content is non-null AND
    /// content-type is text).
    /// </summary>
    internal static PredicateExpression<Node> CandidatePredicate() {
        PredicateExpression<Node> hasName = new PredicateExpression<Node>(n => n.Name != null && n.Name != "");

        string[] prefixes = TextContentTypePredicate.TextPrefixes;
        string p0 = prefixes[0] + "%"; // "text/%"
        string p1 = prefixes[1] + "%"; // "application/json%"
        string p2 = prefixes[2] + "%"; // "application/xml%"

        PredicateExpression<Node> isTextType =
            new PredicateExpression<Node>(n => n.ContentType.Like(p0)) |
            new PredicateExpression<Node>(n => n.ContentType.Like(p1)) |
            new PredicateExpression<Node>(n => n.ContentType.Like(p2));

        PredicateExpression<Node> hasTextContent =
            new PredicateExpression<Node>(n => n.Content != null) & isTextType;

        return hasName | hasTextContent;
    }

    /// <summary>
    /// runs the backfill: re-embeds every node with any embeddable surface using
    /// the configured <see cref="IEmbeddingProvider"/>.
    /// </summary>
    /// <param name="ct">cancellation token</param>
    public async Task RunAsync(CancellationToken ct = default) {
        if (!embeddingProvider.IsEnabled) {
            logger.LogInformation("event=backfill.skip reason=provider_disabled message=\"embedding provider is disabled; backfill is a no-op\"");
            return;
        }

        logger.LogInformation("event=backfill.start version=v2 composition=name_plus_content");
        DateTimeOffset started = DateTimeOffset.UtcNow;

        long total = await database.Load<Node>(DB.Count())
                                   .Where(CandidatePredicate().Content)
                                   .ExecuteScalarAsync<long>();

        logger.LogInformation("event=backfill.candidates total={Total}", total);

        int embedded = 0;

        await foreach (Node node in database.Load<Node>(n => n.Id)
                                            .Where(CandidatePredicate().Content)
                                            .ExecuteEntitiesAsync()) {
            ct.ThrowIfCancellationRequested();

            using Transaction transaction = database.Transaction();
            await embeddingProvider.RegenerateEmbedding(database, transaction, node.Id, ct);
            transaction.Commit();

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
