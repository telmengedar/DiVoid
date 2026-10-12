using System;
using System.Collections.Generic;
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
/// walks nodes with an embeddable surface (non-empty name OR text content) and (re)generates their embedding using the current composition policy.
/// Postgres-only: if <see cref="IEmbeddingCapability.IsEnabled"/> is false the method
/// exits immediately with a log message and no database writes.
/// </summary>
public class EmbeddingBackfillService(IEntityManager database, IEmbeddingCapability embeddingCapability, ILogger<EmbeddingBackfillService> logger) {

    const int ProgressInterval = 25;

    readonly IEntityManager database = database;
    readonly IEmbeddingCapability embeddingCapability = embeddingCapability;
    readonly ILogger<EmbeddingBackfillService> logger = logger;

    static PredicateExpression<Node> CandidatePredicate(long[] ids, bool longOnly) {
        PredicateExpression<Node> candidates = new PredicateExpression<Node>(n => n.Name != null && n.Name != "") |
                                               new PredicateExpression<Node>(n => n.Content != null);

        if (ids != null)
            candidates &= new PredicateExpression<Node>(n => n.Id.In(ids));

        if (longOnly)
            candidates &= LongOnlyPredicate();

        return candidates;
    }

    static PredicateExpression<Node> LongOnlyPredicate() {
        PredicateExpression<Node> namedOverflow = new PredicateExpression<Node>(n => n.Name != null && n.Name != "" && DB.CustomFunction("length", DB.Property<Node>(x => x.Name)).Single * 2 + 2 + DB.CustomFunction("length", DB.Property<Node>(x => x.Content)).Single > EmbeddingInputComposer.MaxLength);
        PredicateExpression<Node> unnamedOverflow = new PredicateExpression<Node>(n => (n.Name == null || n.Name == "") && DB.CustomFunction("length", DB.Property<Node>(x => x.Content)).Single > EmbeddingInputComposer.MaxLength);
        return namedOverflow | unnamedOverflow;
    }

    /// <summary>
    /// re-embeds the selected nodes using the name+content composition policy.
    /// without options every node with an embeddable surface is selected.
    /// </summary>
    /// <param name="ids">when set, only nodes with one of these ids are selected</param>
    /// <param name="longOnly">when true, only nodes whose name and content may exceed the composition length are selected</param>
    /// <param name="ct">cancellation token</param>
    public async Task RunAsync(long[] ids = null, bool longOnly = false, CancellationToken ct = default) {
        logger.LogInformation("event=backfill.options ids={IdCount} long_only={LongOnly}", ids?.Length ?? 0, longOnly);

        if (!embeddingCapability.IsEnabled) {
            logger.LogInformation("event=backfill.skip reason=capability_disabled message=\"embedding capability is disabled; backfill is a no-op\"");
            return;
        }

        logger.LogInformation("event=backfill.start version=v2 composition=name_plus_content");
        DateTimeOffset started = DateTimeOffset.UtcNow;

        PredicateExpression<Node> predicate = CandidatePredicate(ids, longOnly);

        List<Node> loaded = [];
        await foreach (Node node in database.Load<Node>(n => n.Id, n => n.Name, n => n.ContentType, n => n.LastUpdate)
                                            .Where(predicate.Content)
                                            .OrderBy(n => n.Id)
                                            .ExecuteEntitiesAsync()) {
            ct.ThrowIfCancellationRequested();

            if (TextContentTypePredicate.IsText(node.ContentType) || (!longOnly && !string.IsNullOrEmpty(node.Name)))
                loaded.Add(node);
        }

        Node[] candidates = loaded.ToArray();

        logger.LogInformation("event=backfill.candidates total={Total}", candidates.Length);

        int embedded = 0;
        int skippedChanged = 0;

        foreach (Node candidate in candidates) {
            ct.ThrowIfCancellationRequested();

            byte[] content = TextContentTypePredicate.IsText(candidate.ContentType) ? await LoadContent(candidate.Id) : null;
            long written = await EmbeddingWrite.Build(database, candidate.Id, candidate.Name, content, candidate.ContentType, candidate.LastUpdate)
                                              .ExecuteAsync();

            if (written == 0)
                skippedChanged++;
            else
                embedded++;

            if ((embedded + skippedChanged) % ProgressInterval == 0)
                logger.LogInformation("event=backfill.progress embedded={Embedded} skipped_changed={SkippedChanged} of={Total}", embedded, skippedChanged, candidates.Length);
        }

        TimeSpan elapsed = DateTimeOffset.UtcNow - started;
        logger.LogInformation(
            "event=backfill.complete embedded={Embedded} skipped_changed={SkippedChanged} total={Total} elapsed={Elapsed}",
            embedded, skippedChanged, candidates.Length, elapsed);
    }

    async Task<byte[]> LoadContent(long nodeId) {
        Node node = await database.Load<Node>(n => n.Content)
                                  .Where(n => n.Id == nodeId)
                                  .ExecuteEntityAsync();
        return node?.Content;
    }
}
