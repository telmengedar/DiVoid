using System.Linq.Expressions;
using Backend.Extensions;
using Backend.Models.Nodes;
using Backend.Models.Users;
using Backend.Query;
using Backend.Services.Embeddings;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Errors.Exceptions;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Entities.Operations.Prepared;
using Pooshit.Ocelot.Expressions;
using Pooshit.Ocelot.Fields;
using Pooshit.Ocelot.Tokens;

namespace Backend.Services.Nodes;

/// <inheritdoc />
public class NodeService(IEntityManager database, IEmbeddingCapability embeddingCapability) : INodeService
{
    readonly IEntityManager database = database;
    readonly IEmbeddingCapability embeddingCapability = embeddingCapability;

    /// <summary>
    /// computes the golden-angle auto-position offset for a new node given its id and an anchor position.
    /// angle = (id * 2.4) mod (2π), radius = 200.
    /// deterministic per id — same id always produces the same offset from the anchor.
    /// </summary>
    static (double X, double Y) ComputeAutoPosition(long nodeId, double anchorX, double anchorY)
    {
        const double radius = 200.0;
        double angle = (nodeId * 2.4) % (2.0 * Math.PI);
        return (anchorX + radius * Math.Cos(angle), anchorY + radius * Math.Sin(angle));
    }

    /// <summary>
    /// if the candidate node is at (0, 0) and any of the anchor candidates has a non-zero position,
    /// moves the candidate to the golden-angle offset from the first positioned anchor.
    /// no-op when the candidate is already positioned, or when no positioned anchor is found.
    /// all DB operations are bound to the supplied transaction.
    /// </summary>
    async Task TryAnchorOrphanToPositioned(Transaction transaction, long candidateId, long[] anchorCandidates)
    {
        long[] ids = new long[anchorCandidates.Length + 1];
        ids[0] = candidateId;
        anchorCandidates.CopyTo(ids, 1);

        Dictionary<long, Node> nodesById = [];
        await foreach (Node n in database.Load<Node>(n => n.Id, n => n.X, n => n.Y)
                                          .Where(n => n.Id.In(ids))
                                          .ExecuteEntitiesAsync(transaction))
        {
            nodesById[n.Id] = n;
        }

        if (!nodesById.TryGetValue(candidateId, out Node candidate))
            return;
        if (candidate.X != 0.0 || candidate.Y != 0.0)
            return;

        Node anchor = null;
        foreach (long anchorId in anchorCandidates)
        {
            if (nodesById.TryGetValue(anchorId, out Node a) && (a.X != 0.0 || a.Y != 0.0))
            {
                anchor = a;
                break;
            }
        }

        if (anchor == null)
            return;

        (double newX, double newY) = ComputeAutoPosition(candidateId, anchor.X, anchor.Y);
        await database.Update<Node>()
                      .Set(n => n.X == newX, n => n.Y == newY)
                      .Where(n => n.Id == candidateId)
                      .ExecuteAsync(transaction);
    }

    /// <summary>
    /// resolves a type name to its <see cref="NodeType"/> id, creating the row if it does not exist.
    /// <paramref name="type"/> that is null, empty, or whitespace-only normalizes to the untyped
    /// marker (null), so an empty-string type never spawns a row distinct from the null-named one.
    /// the untyped lookup branches on a literal <c>== null</c> rather than a captured-null variable:
    /// a captured null compiles to <c>Type = NULL</c> (never true), which is why stray null-named
    /// rows accumulated before (DiVoid #508); the literal emits <c>Type IS NULL</c> and reuses the row.
    /// all operations are bound to <paramref name="transaction"/>.
    /// </summary>
    async Task<long> ResolveOrCreateTypeId(string type, Transaction transaction)
    {
        string normalized = string.IsNullOrWhiteSpace(type) ? null : type;

        LoadOperation<NodeType> lookup = normalized == null
            ? database.Load<NodeType>(t => t.Id).Where(t => t.Type == null)
            : database.Load<NodeType>(t => t.Id).Where(t => t.Type == normalized);
        long? existing = await lookup.ExecuteScalarAsync<long?>(transaction);
        if (existing.HasValue)
            return existing.Value;

        return await database.Insert<NodeType>()
                             .Columns(n => n.Type)
                             .Values(normalized)
                             .ReturnID()
                             .ExecuteAsync(transaction);
    }

    /// <inheritdoc />
    public async Task<NodeDetails> CreateNode(NodeDetails node, long callerId)
    {
        using Transaction transaction = database.Transaction();
        long typeId = await ResolveOrCreateTypeId(node.Type, transaction);

        double insertX = node.X ?? 0.0;
        double insertY = node.Y ?? 0.0;
        DateTime now = DateTime.UtcNow;
        long nodeId = await database.Insert<Node>()
                              .Columns(n => n.TypeId, n => n.Name, n => n.Status, n => n.Severity, n => n.X, n => n.Y, n => n.OwnerId, n => n.Access, n => n.Created, n => n.LastUpdate)
                              .Values(typeId, node.Name, node.Status, node.Severity, insertX, insertY, callerId, node.Access ?? (NodeAccess.Read | NodeAccess.Write), now, now)
                              .ReturnID()
                              .ExecuteAsync(transaction);

        // create any requested links atomically with the node
        long[] links = node.Links;
        if (links?.Length > 0)
        {
            foreach (long targetId in links)
            {
                await database.Insert<NodeLink>()
                              .Columns(l => l.SourceId, l => l.TargetId)
                              .Values(nodeId, targetId)
                              .ExecuteAsync(transaction);
            }

            // auto-position: only when caller did not explicitly set a position (both X and Y are 0 or absent)
            if (insertX == 0.0 && insertY == 0.0)
                await TryAnchorOrphanToPositioned(transaction, nodeId, links);
        }

        if (embeddingCapability.IsEnabled && !string.IsNullOrWhiteSpace(node.Name))
        {
            string nameInput = node.Name.Length > EmbeddingInputComposer.MaxLength
                ? node.Name[..EmbeddingInputComposer.MaxLength]
                : node.Name;

            await database.Update<Node>()
                          .Set(n => n.Embedding == DB.CustomFunction("embedding",
                                                                      DB.Constant(TextContentTypePredicate.EmbeddingModel),
                                                                      DB.Constant(nameInput)).Type<float[]>())
                          .Where(n => n.Id == nodeId)
                          .ExecuteAsync(transaction);
        }

        transaction.Commit();
        return await GetNodeById(nodeId, callerId, isAdmin: true);
    }

    /// <inheritdoc />
    public async Task<AsyncPageResponseWriter<TypeListItem>> ListTypes(CancellationToken ct = default)
    {
        TypeListMapper mapper = new();
        LoadOperation<NodeType> operation = mapper.CreateOperation(database, mapper.DefaultListFields);
        operation.GroupBy(DB.Property<NodeType>(t => t.Id, "type"), DB.Property<NodeType>(t => t.Type, "type"));
        operation.OrderBy(
            new OrderByCriteria(DB.Count(DB.Property<Node>(n => n.Id, "node")), ascending: false),
            new OrderByCriteria(DB.Property<NodeType>(t => t.Type, "type"), ascending: true));

        WindowResult<TypeListItem, long> windowed =
            await mapper.WindowedFromOperation<long, NodeType>(operation, DB.CountOver(), ct, mapper.DefaultListFields);

        return new AsyncPageResponseWriter<TypeListItem>(
            windowed.Items,
            () => windowed.WindowValue,
            null
        );
    }


    /// <inheritdoc />
    public async Task Delete(long nodeId, long callerId, bool isAdmin)
    {
        PredicateExpression<Node> gate = NodeAuthorization.BuildVisibilityPredicate(callerId, isAdmin, write: true);
        PredicateExpression<Node> predicate = new PredicateExpression<Node>(n => n.Id == nodeId);
        if (gate != null)
            predicate &= gate;

        using Transaction transaction = database.Transaction();
        if (await database.Delete<Node>()
                         .Where(predicate.Content)
                         .ExecuteAsync(transaction) == 0)
            throw new NotFoundException<Node>(nodeId);
        await database.Delete<NodeLink>().Where(l => l.SourceId == nodeId || l.TargetId == nodeId).ExecuteAsync(transaction);
        transaction.Commit();
    }

    /// <inheritdoc />
    public async Task<(string, Stream)> GetNodeData(long nodeId, long callerId, bool isAdmin)
    {
        PredicateExpression<Node> gate = NodeAuthorization.BuildVisibilityPredicate(callerId, isAdmin, write: false);
        PredicateExpression<Node> predicate = new PredicateExpression<Node>(n => n.Id == nodeId);
        if (gate != null)
            predicate &= gate;

        Node node = await database.Load<Node>(n => n.ContentType, n => n.Content)
                                .Where(predicate.Content)
                                .ExecuteEntityAsync();
        if (node == null)
            throw new NotFoundException<Node>(nodeId);

        if (node.Content == null)
            throw new NotFoundException<Node>($"Node '{nodeId}' has no content");

        return (node.ContentType, new MemoryStream(node.Content));
    }

    /// <inheritdoc />
    public async Task LinkNodes(long sourceNodeId, long targetNodeId, long callerId, bool isAdmin)
    {
        if (sourceNodeId == targetNodeId)
            throw new InvalidOperationException("Unable to link node to itself");

        PredicateExpression<Node> gate = NodeAuthorization.BuildVisibilityPredicate(callerId, isAdmin, write: true);
        PredicateExpression<Node> sourcePredicate = new PredicateExpression<Node>(n => n.Id == sourceNodeId);
        if (gate != null)
            sourcePredicate &= gate;

        if (await database.Load<Node>(DB.Count()).Where(sourcePredicate.Content).ExecuteScalarAsync<long>() == 0)
            throw new NotFoundException<Node>(sourceNodeId);

        using Transaction transaction = database.Transaction();
        if (await database.Load<Node>(DB.Count()).Where(n => n.Id == sourceNodeId || n.Id == targetNodeId).ExecuteScalarAsync<long>(transaction) != 2)
            throw new NotFoundException<Node>($"Either '{sourceNodeId}' or '{targetNodeId}' does not exist");
        if (await database.Load<NodeLink>(DB.Count()).Where(n => n.SourceId == sourceNodeId && n.TargetId == targetNodeId || n.SourceId == targetNodeId && n.TargetId == sourceNodeId).ExecuteScalarAsync<long>(transaction) > 0)
            return;
        await database.Insert<NodeLink>()
                      .Columns(n => n.SourceId, n => n.TargetId)
                      .Values(sourceNodeId, targetNodeId)
                      .ExecuteAsync(transaction);
        await TryAnchorOrphanToPositioned(transaction, sourceNodeId, [targetNodeId]);
        await TryAnchorOrphanToPositioned(transaction, targetNodeId, [sourceNodeId]);
        transaction.Commit();
    }

    /// <summary>
    /// builds a predicate for a single hop segment.
    /// Handles id, type, name, status — with wildcard LIKE semantics on name/status.
    /// </summary>
    Expression<Func<Node, bool>> GenerateHopFilter(HopSegment hop)
    {
        PredicateExpression<Node> predicate = null;

        foreach (HopPredicate p in hop.Predicates)
        {
            switch (p.Key)
            {
                case "id":
                    long[] ids = Array.ConvertAll(p.Values, v => long.Parse(v));
                    predicate &= n => n.Id.In(ids);
                    break;

                case "name":
                    if (p.Values.Any(v => v.ContainsWildcards()))
                    {
                        PredicateExpression<Node> namePredicate = null;
                        foreach (string nameFilter in p.Values)
                            namePredicate |= n => n.Name.Like(nameFilter);
                        predicate &= namePredicate;
                    } else
                    {
                        predicate &= n => n.Name.In(p.Values);
                    }
                    break;

                case "type":
                {
                    LoadOperation<NodeType> typeOp = database.Load<NodeType>(t => t.Id)
                                                             .Where(t => t.Type.In(p.Values));
                    predicate &= n => n.TypeId.In(typeOp);
                    break;
                }

                case "status":
                    if (p.Values.Any(v => v.ContainsWildcards()))
                    {
                        PredicateExpression<Node> statusPredicate = null;
                        foreach (string statusFilter in p.Values)
                            statusPredicate |= n => n.Status.Like(statusFilter);
                        predicate &= statusPredicate;
                    } else
                    {
                        predicate &= n => n.Status.In(p.Values);
                    }
                    break;

                case "severity":
                {
                    int[] severityValues = Array.ConvertAll(p.Values, v => int.Parse(v));
                    predicate &= n => n.Severity.In(severityValues);
                    break;
                }
            }
        }

        return predicate?.Content;
    }

    /// <summary>
    /// original single-filter method — builds predicate from filter fields and applies visibility gate.
    /// </summary>
    internal Expression<Func<Node, bool>> GenerateFilter(NodeFilter filter, long callerId, bool isAdmin)
    {
        PredicateExpression<Node> predicate = null;

        PredicateExpression<Node> visibility = NodeAuthorization.BuildVisibilityPredicate(callerId, isAdmin);
        if (visibility != null)
            predicate &= visibility;

        if (filter.Id?.Length > 0)
            predicate &= n => n.Id.In(filter.Id);

        if (filter.Name?.Length > 0)
            if (filter.Name.Any(n => n.ContainsWildcards()))
            {
                PredicateExpression<Node> namePredicate = null;
                foreach (string nameFilter in filter.Name)
                    namePredicate |= n => n.Name.Like(nameFilter);
                predicate &= namePredicate;
            } else
            {
                predicate &= n => n.Name.In(filter.Name);
            }

        if (filter.Type?.Length > 0)
        {
            LoadOperation<NodeType> typeOp = database.Load<NodeType>(t => t.Id)
                                                     .Where(t => t.Type.In(filter.Type));
            if (filter.NoType)
            {
                LoadOperation<NodeType> nullTypeOp = database.Load<NodeType>(t => t.Id)
                                                             .Where(t => t.Type == null);
                predicate &= new PredicateExpression<Node>(n => n.TypeId.In(typeOp))
                           | new PredicateExpression<Node>(n => n.TypeId.In(nullTypeOp));
            } else
            {
                predicate &= n => n.TypeId.In(typeOp);
            }
        } else if (filter.NoType)
        {
            LoadOperation<NodeType> nullTypeOp = database.Load<NodeType>(t => t.Id)
                                                         .Where(t => t.Type == null);
            predicate &= n => n.TypeId.In(nullTypeOp);
        }

        if (filter.Status?.Length > 0)
        {
            PredicateExpression<Node> statusValuePredicate;
            if (filter.Status.Any(s => s.ContainsWildcards()))
            {
                statusValuePredicate = null;
                foreach (string statusFilter in filter.Status)
                    statusValuePredicate |= n => n.Status.Like(statusFilter);
            } else
            {
                statusValuePredicate = new PredicateExpression<Node>(n => n.Status.In(filter.Status));
            }

            if (filter.NoStatus)
            {
                // When both status=<list> and nostatus=true are present, the intent is OR:
                // "give me nodes whose status is in the list OR nodes with no status."
                // ANDing the two predicates would yield an impossible WHERE clause.
                predicate &= statusValuePredicate | new PredicateExpression<Node>(n => n.Status == null || n.Status == "");
            } else
            {
                predicate &= statusValuePredicate;
            }
        } else if (filter.NoStatus)
        {
            predicate &= n => n.Status == null || n.Status == "";
        }

        if (filter.Severity?.Length > 0)
        {
            PredicateExpression<Node> severityValuePredicate =
                new PredicateExpression<Node>(n => n.Severity.In(filter.Severity));

            if (filter.NoSeverity)
            {
                predicate &= severityValuePredicate | new PredicateExpression<Node>(n => n.Severity == null);
            } else
            {
                predicate &= severityValuePredicate;
            }
        } else if (filter.NoSeverity)
        {
            predicate &= n => n.Severity == null;
        }

        if (filter.SeverityMin.HasValue)
        {
            int severityMin = filter.SeverityMin.Value;
            predicate &= n => n.Severity >= severityMin;
        }

        if (filter.SeverityMax.HasValue)
        {
            int severityMax = filter.SeverityMax.Value;
            predicate &= n => n.Severity <= severityMax;
        }

        if (filter.Bounds?.Length == 4)
        {
            double xMin = filter.Bounds[0];
            double yMin = filter.Bounds[1];
            double xMax = filter.Bounds[2];
            double yMax = filter.Bounds[3];
            predicate &= n => n.X >= xMin && n.X <= xMax && n.Y >= yMin && n.Y <= yMax;
        }

        if (filter.CreatedFrom.HasValue)
        {
            DateTime createdFrom = filter.CreatedFrom.Value;
            predicate &= n => n.Created >= createdFrom;
        }

        if (filter.CreatedTo.HasValue)
        {
            DateTime createdTo = filter.CreatedTo.Value;
            predicate &= n => n.Created < createdTo;
        }

        if (filter.UpdatedFrom.HasValue)
        {
            DateTime updatedFrom = filter.UpdatedFrom.Value;
            predicate &= n => n.LastUpdate >= updatedFrom;
        }

        if (filter.UpdatedTo.HasValue)
        {
            DateTime updatedTo = filter.UpdatedTo.Value;
            predicate &= n => n.LastUpdate < updatedTo;
        }

        return predicate?.Content;
    }

    /// <summary>
    /// builds the linkedto predicate for <paramref name="operation"/> and returns the
    /// predicate fragment to AND into the caller's combined <c>Where</c> expression.
    ///
    /// on databases that support LATERAL JOIN (Postgres, MySQL, MSSQL) a correlated
    /// subquery is attached to <paramref name="operation"/> as a side effect and
    /// only the neighbour-exclude predicate (<c>!n.Id.In(linkedTo)</c>) is returned.
    /// on SQLite the existing UNION-of-two-directions shape is used as a fallback
    /// (per Ocelot architecture #537 §8.5.2).
    ///
    /// the caller must AND the returned expression into the final predicate before
    /// calling <c>Where</c> on the operation — do not call <c>Where</c> a second time
    /// after this method, as Ocelot replaces the existing clause on each call.
    /// </summary>
    PredicateExpression<Node> BuildLinkedToFilter(LoadOperation<Node> operation, long[] linkedTo)
    {
        if (database.DBClient.DBInfo.SupportsLateralJoin)
        {
            LoadOperation<NodeLink> lateral = database.Load<NodeLink>(l => l.SourceId, l => l.TargetId)
                .Where(l => (l.SourceId == DB.Property<Node>(n => n.Id, "node").Int64
                          || l.TargetId == DB.Property<Node>(n => n.Id, "node").Int64)
                         && (l.SourceId.In(linkedTo) || l.TargetId.In(linkedTo)))
                .Limit(1);
            operation.LateralJoin(lateral, joinAlias: "link");
            return new PredicateExpression<Node>(n => !n.Id.In(linkedTo));
        }
        else
        {
            LoadOperation<NodeLink> linkOp = database.Load<NodeLink>(l => l.SourceId)
                                                     .Where(l => l.SourceId.In(linkedTo) || l.TargetId.In(linkedTo))
                                                     .Union(database.Load<NodeLink>(n => n.TargetId)
                                                                    .Where(l => l.SourceId.In(linkedTo) || l.TargetId.In(linkedTo)));
            return new PredicateExpression<Node>(n => n.Id.In(linkOp) && !n.Id.In(linkedTo));
        }
    }

    /// <summary>
    /// builds the link sub-query for both directions of <see cref="NodeLink"/> from
    /// a <paramref name="prevHopOp"/> id-subquery.  This is the same Union-of-two-directions
    /// pattern used by the original <c>linkedto</c> filter, generalized to accept a
    /// server-side subquery instead of a materialized id-array so the entire chain
    /// composes into a single SQL statement.
    /// </summary>
    LoadOperation<NodeLink> BuildLinkSubquery(LoadOperation<Node> prevHopOp)
    {
        return database.Load<NodeLink>(l => l.SourceId)
                       .Where(l => l.SourceId.In(prevHopOp) || l.TargetId.In(prevHopOp))
                       .Union(database.Load<NodeLink>(l => l.TargetId)
                                      .Where(l => l.SourceId.In(prevHopOp) || l.TargetId.In(prevHopOp)));
    }

    /// <summary>
    /// result of composing hops: the terminal <see cref="LoadOperation{Node}"/> ready for
    /// paged execution.  The separate <c>TerminalPredicate</c> field is no longer needed
    /// now that <c>ExecutePagedAsync</c> supplies the total in the same query.
    /// </summary>
    readonly record struct ComposedPath(
        LoadOperation<Node> Terminal
    );

    /// <summary>
    /// chains N hops into a terminal <see cref="LoadOperation{Node}"/> ready for
    /// <c>ExecutePagedAsync</c>.
    ///
    /// Every hop — including all intermediate hops — is expressed as a server-side
    /// <c>IN (subquery)</c> predicate.  No intermediate id-sets are materialised; the
    /// entire chain compiles into a single SQL statement executed once by the database.
    ///
    /// Pattern mirrors the existing <c>linkedto</c> filter at <c>NodeService.cs:175-181</c>:
    /// each hop is a <see cref="LoadOperation{Node}"/> whose predicate wraps the previous
    /// hop's id-subquery via a Union-of-two-<see cref="NodeLink"/>-directions subquery.
    ///
    /// When <paramref name="filter"/> carries a non-empty <c>Query</c> the terminal
    /// operation receives the same similarity ordering and predicates as plain-list mode
    /// (see <see cref="ApplySemanticSearch"/>).
    /// </summary>
    ComposedPath ComposeHops(PathQuery query, NodePathFilter filter, NodeMapper mapper, bool isSemantic, long callerId, bool isAdmin, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        HopSegment[] hops = query.Hops;
        filter.Fields ??= mapper.DefaultListFields;

        // hop[0]: seed — plain filter on Node, no traversal
        LoadOperation<Node> currentHopOp = database.Load<Node>(n => n.Id);
        Expression<Func<Node, bool>> seedPred = GenerateHopFilter(hops[0]);
        if (seedPred != null)
            currentHopOp.Where(seedPred);

        // hop[1..N-1]: each intermediate hop chains via IN (link-subquery(prevHop))
        for (int i = 1; i < hops.Length; i++)
        {
            ct.ThrowIfCancellationRequested();

            LoadOperation<Node> prevHopOp = currentHopOp;
            LoadOperation<NodeLink> linkOp = BuildLinkSubquery(prevHopOp);

            PredicateExpression<Node> chainPredicate = null;
            chainPredicate &= n => n.Id.In(linkOp) && !n.Id.In(prevHopOp);

            Expression<Func<Node, bool>> hopPred = GenerateHopFilter(hops[i]);
            if (hopPred != null)
                chainPredicate &= new PredicateExpression<Node>(hopPred);

            currentHopOp = database.Load<Node>(n => n.Id).Where(chainPredicate.Content);
        }

        // The terminal operation uses the mapper join (Node + NodeType) and sort
        Expression<Func<Node, bool>> terminalPredicate = hops.Length == 1
            ? GenerateHopFilter(hops[0])
            : n => n.Id.In(currentHopOp);

        // standard filter predicates (status, type, name, id, nostatus, linkedto, bounds) compose
        // with the path-resolved terminal set — same pipeline as the plain-list branch.
        // Both predicates are AND-ed into a single Where call: Ocelot's LoadOperation.Where
        // replaces the existing clause on each call, so callers must combine manually.
        Expression<Func<Node, bool>> standardPredicate = GenerateFilter(filter, callerId, isAdmin);
        PredicateExpression<Node> combined = null;
        if (terminalPredicate != null)
            combined &= new PredicateExpression<Node>(terminalPredicate);
        if (standardPredicate != null)
            combined &= new PredicateExpression<Node>(standardPredicate);

        LoadOperation<Node> terminal = mapper.CreateOperation(database, filter.Fields);
        terminal.ApplyFilter(filter, mapper);
        if (filter.LinkedTo?.Length > 0)
            combined &= BuildLinkedToFilter(terminal, filter.LinkedTo);

        // semantic predicates ANDed into combined BEFORE the single Where() call —
        // mirrors the fix applied to ListPaged: type/status/linkedto filters survive.
        if (isSemantic)
            ApplySemanticSearch(terminal, filter, mapper, ref combined);

        terminal.Where(combined?.Content);

        return new(terminal);
    }

    /// <summary>
    /// ANDs semantic-search predicates into <paramref name="predicate"/> and overrides the
    /// ORDER BY on <paramref name="operation"/> when <paramref name="filter"/> carries a
    /// non-empty <c>Query</c>.
    ///
    /// Both plain-list (<see cref="ListPaged"/>) and path-query (<see cref="ListPagedByPath"/>)
    /// terminal operations go through this helper so the similarity treatment is identical in
    /// both modes (per architectural doc §10).
    ///
    /// The caller is responsible for calling <c>operation.Where(predicate?.Content)</c> exactly
    /// once after this method returns — combining the semantic predicates with the caller's own
    /// filter predicates in a single <c>Where</c> call.  This follows the "combine manually,
    /// single Where call" contract (Ocelot replaces the existing clause on each Where call).
    ///
    /// When <c>Query</c> is present this method:
    /// <list type="bullet">
    ///   <item>ANDs <c>n.Embedding IS NOT NULL</c> into <paramref name="predicate"/> to exclude un-embedded nodes</item>
    ///   <item>ANDs <c>similarity &gt;= MinSimilarity</c> into <paramref name="predicate"/> when a floor is supplied</item>
    ///   <item>removes any sort that <see cref="FilterExtensions.ApplyFilter{T,TEntity}"/> may have added and
    ///         replaces it with <c>ORDER BY similarity DESC, id ASC</c></item>
    /// </list>
    /// </summary>
    internal void ApplySemanticSearch(LoadOperation<Node> operation, NodeFilter filter, NodeMapper mapper, ref PredicateExpression<Node> predicate)
    {
        // exclude nodes without an embedding — they have no vector signal to rank on.
        // ANDed into the caller's predicate, NOT via a separate Where() call, so that
        // type/status/linkedto/id/name filters (already in predicate) are preserved.
        predicate &= new PredicateExpression<Node>(n => n.Embedding != null);

        // optional caller-supplied similarity floor; .Single is the typed float placeholder
        // for use in lambda expressions (IDBField pattern from mamgo CampaignItemTargetService)
        if (filter.MinSimilarity.HasValue)
        {
            float floor = filter.MinSimilarity.Value;
            predicate &= new PredicateExpression<Node>(n => mapper["similarity"].Field.Single >= floor);
        }

        // similarity DESC, id ASC — replaces any sort that ApplyFilter may have set.
        // ascending=false → DESC, ascending=true → ASC (OrderByCriteria convention)
        operation.OrderBy(
            new OrderByCriteria(mapper["similarity"].Field, ascending: false),
            new OrderByCriteria(mapper["id"].Field, ascending: true));
    }

    /// <summary>
    /// validates the <c>Bounds</c> array on <paramref name="filter"/> if present.
    /// throws <see cref="InvalidOperationException"/> (→ HTTP 400) when the array
    /// is present but has a length other than 4, or when xMin &gt; xMax / yMin &gt; yMax.
    /// </summary>
    static void ValidateBounds(NodeFilter filter)
    {
        if (filter.Bounds == null)
            return;
        if (filter.Bounds.Length != 4)
            throw new ArgumentException("bounds must contain exactly 4 values: xMin,yMin,xMax,yMax", "bounds");
        if (filter.Bounds[0] > filter.Bounds[2])
            throw new ArgumentException("bounds xMin must be less than or equal to xMax", "bounds");
        if (filter.Bounds[1] > filter.Bounds[3])
            throw new ArgumentException("bounds yMin must be less than or equal to yMax", "bounds");
    }


    /// <summary>Single batched secondary query — never call per-row.</summary>
    async Task<Dictionary<long, long[]>> FetchAdjacentIds(long[] ids, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Dictionary<long, List<long>> adjacency = [];
        foreach (long id in ids)
            adjacency[id] = [];

        await foreach (NodeLink link in database.Load<NodeLink>(l => l.SourceId, l => l.TargetId)
                                                .Where(l => l.SourceId.In(ids) || l.TargetId.In(ids))
                                                .ExecuteEntitiesAsync())
        {
            if (link.SourceId == link.TargetId)
                continue;

            if (adjacency.TryGetValue(link.SourceId, out List<long> srcList))
                srcList.Add(link.TargetId);
            if (adjacency.TryGetValue(link.TargetId, out List<long> tgtList))
                tgtList.Add(link.SourceId);
        }

        Dictionary<long, long[]> result = [];
        foreach ((long id, List<long> neighbors) in adjacency)
            result[id] = [.. neighbors];
        return result;
    }

    /// <summary>Rows with no incident links receive an empty array, not null.</summary>
    async Task<List<NodeDetails>> MaterializeWithLinks(IAsyncEnumerable<NodeDetails> items, CancellationToken ct)
    {
        List<NodeDetails> rows = [];
        await foreach (NodeDetails node in items)
            rows.Add(node);

        if (rows.Count == 0)
            return rows;

        long[] ids = new long[rows.Count];
        for (int i = 0; i < rows.Count; i++)
            ids[i] = rows[i].Id;

        Dictionary<long, long[]> adjacency = await FetchAdjacentIds(ids, ct);
        foreach (NodeDetails node in rows)
            node.Links = adjacency.TryGetValue(node.Id, out long[] neighbors) ? neighbors : [];

        return rows;
    }

    /// <inheritdoc />
    public async Task<AsyncPageResponseWriter<NodeDetails>> ListPaged(NodeFilter filter, long callerId, bool isAdmin, CancellationToken ct = default)
    {
        filter ??= new();

        ValidateBounds(filter);

        bool isSemantic = !string.IsNullOrWhiteSpace(filter.Query);

        // guard: minSimilarity without query is a caller error
        if (!isSemantic && filter.MinSimilarity.HasValue)
            throw new SemanticSearchUnavailableException("minSimilarity requires query");

        // guard: semantic search requires Postgres (the embedding() function)
        if (isSemantic && !embeddingCapability.IsEnabled)
            throw new SemanticSearchUnavailableException(
                "Semantic search requires Postgres; this deployment does not support the embedding function.");

        if (filter.Fields != null
            && filter.Fields.Contains("similarity", StringComparer.OrdinalIgnoreCase)
            && !isSemantic)
            throw new SemanticSearchUnavailableException(
                "Field 'similarity' is only available when a semantic query is provided via '?query='.");

        if (string.Equals(filter.Sort, "content", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("sort=content is not supported");
        if (string.Equals(filter.Sort, "links", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("sort=links is not supported");

        NodeMapper mapper = new(filter);
        filter.Fields ??= isSemantic
            ? [.. mapper.DefaultListFields, "similarity"]
            : mapper.DefaultListFields;

        if (filter.Fields.Contains("content") && !filter.Fields.Contains("contentType"))
            filter.Fields = [.. filter.Fields, "contentType"];

        // links is derived; not in mapper vocab.
        bool includeLinks = filter.Fields.Contains("links");
        if (includeLinks)
            filter.Fields = filter.Fields.Where(f => !string.Equals(f, "links", StringComparison.OrdinalIgnoreCase)).ToArray();

        LoadOperation<Node> operation = mapper.CreateOperation(database, filter.Fields);
        operation.ApplyFilter(filter, mapper);
        PredicateExpression<Node> listPredicate = null;
        Expression<Func<Node, bool>> generatedFilter = GenerateFilter(filter, callerId, isAdmin);
        if (generatedFilter != null)
            listPredicate &= new PredicateExpression<Node>(generatedFilter);
        if (filter.LinkedTo?.Length > 0)
            listPredicate &= BuildLinkedToFilter(operation, filter.LinkedTo);

        // semantic predicates (Embedding IS NOT NULL, MinSimilarity floor) are ANDed into
        // listPredicate here — BEFORE the single Where() call — so that type/status/linkedto
        // filters are not wiped by a second Where() call inside ApplySemanticSearch.
        if (isSemantic)
            ApplySemanticSearch(operation, filter, mapper, ref listPredicate);

        operation.Where(listPredicate?.Content);

        // Single query: COUNT(*) OVER () window function — ApplyFilter already clamps count ≤500
        // and applies limit/offset; WindowedFromOperation decorates with the window column.
        WindowResult<NodeDetails, long> windowed =
            await mapper.WindowedFromOperation<long, Node>(operation, DB.CountOver(), ct, filter.Fields);

        if (includeLinks)
        {
            List<NodeDetails> rows = await MaterializeWithLinks(windowed.Items, ct);
            return new AsyncPageResponseWriter<NodeDetails>(
                rows.ToAsyncEnumerable(),
                () => windowed.WindowValue,
                filter.Continue
            );
        }

        return new AsyncPageResponseWriter<NodeDetails>(
            windowed.Items,
            () => windowed.WindowValue,
            filter.Continue
        );
    }

    /// <inheritdoc />
    public async Task<AsyncPageResponseWriter<NodeDetails>> ListPagedByPath(NodePathFilter filter, long callerId, bool isAdmin, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        filter ??= new();

        ValidateBounds(filter);

        bool isSemantic = !string.IsNullOrWhiteSpace(filter.Query);

        // guard: minSimilarity without query is a caller error
        if (!isSemantic && filter.MinSimilarity.HasValue)
            throw new SemanticSearchUnavailableException("minSimilarity requires query");

        // guard: semantic search requires Postgres (the embedding() function)
        if (isSemantic && !embeddingCapability.IsEnabled)
            throw new SemanticSearchUnavailableException(
                "Semantic search requires Postgres; this deployment does not support the embedding function.");

        if (string.Equals(filter.Sort, "content", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("sort=content is not supported");
        if (string.Equals(filter.Sort, "links", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("sort=links is not supported");

        NodeMapper mapper = new(filter);
        filter.Fields ??= isSemantic
            ? [.. mapper.DefaultListFields, "similarity"]
            : mapper.DefaultListFields;

        if (filter.Fields.Contains("content") && !filter.Fields.Contains("contentType"))
            filter.Fields = [.. filter.Fields, "contentType"];

        // links is derived; not in mapper vocab.
        bool includeLinks = filter.Fields.Contains("links");
        if (includeLinks)
            filter.Fields = filter.Fields.Where(f => !string.Equals(f, "links", StringComparison.OrdinalIgnoreCase)).ToArray();

        // Parse throws PathQueryParseException on syntax/constraint violations (→ HTTP 400)
        PathQuery query = PathQueryParser.Parse(filter.Path);

        ct.ThrowIfCancellationRequested();

        ComposedPath composed = ComposeHops(query, filter, mapper, isSemantic, callerId, isAdmin, ct);

        ct.ThrowIfCancellationRequested();

        // Single query: COUNT(*) OVER () window function — ApplyFilter (inside ComposeHops) already
        // clamps count ≤500 and applies limit/offset; WindowedFromOperation decorates with the window column.
        WindowResult<NodeDetails, long> windowed =
            await mapper.WindowedFromOperation<long, Node>(composed.Terminal, DB.CountOver(), ct, filter.Fields);

        if (includeLinks)
        {
            List<NodeDetails> rows = await MaterializeWithLinks(windowed.Items, ct);
            return new AsyncPageResponseWriter<NodeDetails>(
                rows.ToAsyncEnumerable(),
                () => windowed.WindowValue,
                filter.Continue
            );
        }

        return new AsyncPageResponseWriter<NodeDetails>(
            windowed.Items,
            () => windowed.WindowValue,
            filter.Continue
        );
    }

    static bool TouchesName(PatchOperation[] patches)
    {
        foreach (PatchOperation op in patches)
        {
            if (string.Compare(op.Path, "/name", StringComparison.OrdinalIgnoreCase) != 0)
                continue;
            if (op.Op == "replace" || op.Op == "add" || op.Op == "remove")
                return true;
        }
        return false;
    }

    static bool TouchesPath(PatchOperation[] patches, string path)
    {
        foreach (PatchOperation op in patches)
        {
            if (string.Equals(op.Path, path, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <inheritdoc />
    public async Task<NodeDetails> Patch(long nodeId, PatchOperation[] patches, long callerId, bool isAdmin, CancellationToken ct)
    {
        bool touchesOwnerId = TouchesPath(patches, "/ownerId");
        bool touchesAccess = TouchesPath(patches, "/access");

        PredicateExpression<Node> gatePredicate;
        if (touchesOwnerId)
        {
            if (!isAdmin)
                throw new NotFoundException<Node>(nodeId);
            gatePredicate = null;
        }
        else if (touchesAccess)
        {
            gatePredicate = NodeAuthorization.BuildOwnerPredicate(callerId, isAdmin);
        }
        else
        {
            gatePredicate = NodeAuthorization.BuildVisibilityPredicate(callerId, isAdmin, write: true);
        }

        PredicateExpression<Node> predicate = new PredicateExpression<Node>(n => n.Id == nodeId);
        if (gatePredicate != null)
            predicate &= gatePredicate;

        PatchOperation typeOp = Array.Find(patches, p => string.Equals(p.Path, "/type", StringComparison.OrdinalIgnoreCase));
        PatchOperation[] remainingPatches = typeOp == null ? patches : patches.Where(p => p != typeOp).ToArray();

        bool nameTouched = embeddingCapability.IsEnabled && TouchesName(patches);
        using Transaction transaction = database.Transaction();

        if (remainingPatches.Length > 0)
        {
            if (await database.Update<Node>()
                             .Patch(remainingPatches)
                             .Where(predicate.Content)
                             .ExecuteAsync(transaction) == 0)
                throw new NotFoundException<Node>(nodeId);
        }

        if (typeOp != null)
        {
            long typeId = await ResolveOrCreateTypeId(typeOp.Value as string, transaction);
            long affected = await database.Update<Node>()
                                          .Set(n => n.TypeId == typeId)
                                          .Where(predicate.Content)
                                          .ExecuteAsync(transaction);
            if (remainingPatches.Length == 0 && affected == 0)
                throw new NotFoundException<Node>(nodeId);
        }

        DateTime patchedAt = DateTime.UtcNow;
        await database.Update<Node>()
                      .Set(n => n.LastUpdate == patchedAt)
                      .Where(n => n.Id == nodeId)
                      .ExecuteAsync(transaction);

        if (nameTouched)
        {
            ct.ThrowIfCancellationRequested();
            await RegenerateEmbeddingViaBranches(database, transaction, nodeId, ct);
        }

        transaction.Commit();
        return await GetNodeById(nodeId, callerId, isAdmin: true);
    }

    /// <summary>
    /// issues four mutually-exclusive UPDATE statements that regenerate the embedding column
    /// entirely on the Postgres side — no Content blob is fetched into .NET.
    /// </summary>
    /// <remarks>
    /// implements the four branches of #440 Decision 3 (composition matrix); each branch
    /// corresponds to one row of the truth table.  the WHEREs are mutually exclusive so exactly
    /// one UPDATE writes a row; the others return 0 affected rows and are no-ops.
    ///
    /// text-content detection mirrors <see cref="TextContentTypePredicate.IsText"/>:
    ///   ILIKE 'text/%' covers the text/* family;
    ///   IN (ApplicationTextTypes) covers application/json, application/xml, etc.
    ///
    /// accepted divergence from <see cref="EmbeddingInputComposer.Compose"/>: the C# composer
    /// uses <c>string.IsNullOrWhiteSpace(name)</c>; the SQL form uses
    /// <c>name IS NULL OR name = ''</c>.  a name patched to pure whitespace is treated as
    /// "name present" here but "name absent" by the composer.  the controller layer should
    /// reject pure-whitespace names before they reach this path.
    /// </remarks>
    /// <param name="database">entity manager (shared with the surrounding transaction)</param>
    /// <param name="transaction">the open transaction that wraps the patch UPDATE</param>
    /// <param name="nodeId">id of the row whose embedding is being regenerated</param>
    /// <param name="ct">cancellation token threaded from the controller</param>
    private static async Task RegenerateEmbeddingViaBranches(IEntityManager database, Transaction transaction, long nodeId, CancellationToken ct)
    {
        (UpdateValuesOperation<Node> f1, UpdateValuesOperation<Node> f2,
         UpdateValuesOperation<Node> f3, UpdateValuesOperation<Node> f4) =
            BuildEmbeddingBranchOperations(database, nodeId);

        await f1.ExecuteAsync(transaction);
        ct.ThrowIfCancellationRequested();

        await f2.ExecuteAsync(transaction);
        ct.ThrowIfCancellationRequested();

        await f3.ExecuteAsync(transaction);
        ct.ThrowIfCancellationRequested();

        await f4.ExecuteAsync(transaction);
    }

    /// <summary>
    /// Constructs the four UPDATE operation trees for embedding regeneration.
    /// Single source of truth — called by <see cref="RegenerateEmbeddingViaBranches"/>
    /// (production execution) and directly by <c>EmbeddingPatchSqlShapeTests.RenderAllBranches</c>
    /// (test SQL-shape assertions).  Any change to the SQL shape is
    /// automatically reflected in both callers.
    ///
    /// Branches:
    ///   F1 — name + text content → embed(name ++ '\n\n' ++ LEFT(convert_from(content,'UTF8'),8000))
    ///   F2 — name only, non-text or no content → embed(name)
    ///   F3 — content only, empty/null name, text content → embed(LEFT(convert_from(content,'UTF8'),8000))
    ///   F4 — neither name nor text content → NULL
    /// </summary>
    internal static (UpdateValuesOperation<Node> F1,
                    UpdateValuesOperation<Node> F2,
                    UpdateValuesOperation<Node> F3,
                    UpdateValuesOperation<Node> F4)
        BuildEmbeddingBranchOperations(IEntityManager database, long nodeId)
    {
        string model = TextContentTypePredicate.EmbeddingModel;
        string[] allowlist = TextContentTypePredicate.ApplicationTextTypes;

        UpdateValuesOperation<Node> f1 = database.Update<Node>()
                                                 .Set(n => n.Embedding == DB.CustomFunction("embedding",
                                                                                             DB.Constant(model),
                                                                                             DB.CustomFunction("concat",
                                                                                                 DB.Property<Node>(x => x.Name),
                                                                                                 DB.Constant("\n\n"),
                                                                                                 DB.Left(DB.ConvertFrom(DB.Property<Node>(x => x.Content), "UTF8"), 8000))).Type<float[]>())
                                                 .Where(n => n.Id == nodeId
                                                          && n.Name != null && n.Name != ""
                                                          && (n.ContentType.Like("text/%") || n.ContentType.In(allowlist))
                                                          && n.Content != null);

        UpdateValuesOperation<Node> f2 = database.Update<Node>()
                                                 .Set(n => n.Embedding == DB.CustomFunction("embedding",
                                                                                             DB.Constant(model),
                                                                                             DB.Property<Node>(x => x.Name)).Type<float[]>())
                                                 .Where(n => n.Id == nodeId
                                                          && n.Name != null && n.Name != ""
                                                          && (!(n.ContentType.Like("text/%") || n.ContentType.In(allowlist)) || n.Content == null));

        UpdateValuesOperation<Node> f3 = database.Update<Node>()
                                                 .Set(n => n.Embedding == DB.CustomFunction("embedding",
                                                                                             DB.Constant(model),
                                                                                             DB.Left(DB.ConvertFrom(DB.Property<Node>(x => x.Content), "UTF8"), 8000)).Type<float[]>())
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

    /// <inheritdoc />
    public async Task UnlinkNodes(long sourceNodeId, long targetNodeId, long callerId, bool isAdmin)
    {
        PredicateExpression<Node> gate = NodeAuthorization.BuildVisibilityPredicate(callerId, isAdmin, write: true);
        PredicateExpression<Node> sourcePredicate = new PredicateExpression<Node>(n => n.Id == sourceNodeId);
        if (gate != null)
            sourcePredicate &= gate;

        if (await database.Load<Node>(DB.Count()).Where(sourcePredicate.Content).ExecuteScalarAsync<long>() == 0)
            throw new NotFoundException<Node>(sourceNodeId);

        await database.Delete<NodeLink>()
                      .Where(n => n.SourceId == sourceNodeId && n.TargetId == targetNodeId || n.SourceId == targetNodeId && n.TargetId == sourceNodeId)
                      .ExecuteAsync();
    }

    /// <inheritdoc />
    public async Task UploadContent(long nodeId, string contentType, Stream data, long callerId, bool isAdmin, CancellationToken ct = default)
    {
        byte[] blob = await data.ToByteArray();

        PredicateExpression<Node> gate = NodeAuthorization.BuildVisibilityPredicate(callerId, isAdmin, write: true);
        PredicateExpression<Node> predicate = new PredicateExpression<Node>(n => n.Id == nodeId);
        if (gate != null)
            predicate &= gate;

        using Transaction transaction = database.Transaction();

        DateTime uploadedAt = DateTime.UtcNow;
        if (await database.Update<Node>()
                          .Set(n => n.ContentType == contentType, n => n.Content == blob, n => n.LastUpdate == uploadedAt)
                          .Where(predicate.Content)
                          .ExecuteAsync(transaction) == 0)
            throw new NotFoundException<Node>(nodeId);

        if (embeddingCapability.IsEnabled) {
            ct.ThrowIfCancellationRequested();
            Node row = await database.Load<Node>(n => n.Name)
                                     .Where(n => n.Id == nodeId)
                                     .ExecuteEntityAsync(transaction);

            string composed = EmbeddingInputComposer.Compose(row.Name, blob, contentType);
            if (composed != null) {
                await database.Update<Node>()
                              .Set(n => n.Embedding == DB.CustomFunction("embedding",
                                                                          DB.Constant(TextContentTypePredicate.EmbeddingModel),
                                                                          DB.Constant(composed)).Type<float[]>())
                              .Where(n => n.Id == nodeId)
                              .ExecuteAsync(transaction);
            } else {
                await database.Update<Node>()
                              .Set(n => n.Embedding == (float[]) null)
                              .Where(n => n.Id == nodeId)
                              .ExecuteAsync(transaction);
            }
        }

        transaction.Commit();
    }

    /// <inheritdoc />
    public Task<AsyncPageResponseWriter<NodeLink>> ListLinks(long[] ids, ListFilter filter, CancellationToken ct)
    {
        filter ??= new();
        if (filter.Count is null or > 500)
            filter.Count = 500;

        LoadOperation<NodeLink> operation = database.Load<NodeLink>(l => l.SourceId, l => l.TargetId)
                                                    .Where(l => l.SourceId.In(ids) || l.TargetId.In(ids));
        operation.ApplyFilter(filter);

        LoadOperation<NodeLink> countOp = database.Load<NodeLink>(DB.Count())
                                                  .Where(l => l.SourceId.In(ids) || l.TargetId.In(ids));

        return Task.FromResult(new AsyncPageResponseWriter<NodeLink>(
            operation.ExecuteEntitiesAsync(),
            () => countOp.ExecuteScalarAsync<long>(),
            filter.Continue
        ));
    }


    /// <inheritdoc />
    public async Task<NodeDetails> GetNodeById(long nodeId, long callerId, bool isAdmin)
    {
        NodeMapper mapper = new();
        LoadOperation<Node> operation = mapper.CreateOperation(database);
        PredicateExpression<Node> predicate = new PredicateExpression<Node>(n => n.Id == nodeId);
        PredicateExpression<Node> gate = NodeAuthorization.BuildVisibilityPredicate(callerId, isAdmin, write: false);
        if (gate != null)
            predicate &= gate;
        operation.Where(predicate.Content);
        NodeDetails result = await mapper.EntityFromOperation(operation);
        if (result == null)
            throw new NotFoundException<Node>(nodeId);
        return result;
    }

    /// <inheritdoc />
    public async Task<long> GetUserIdForNode(long nodeId)
    {
        long? userId = await database.Load<User>(u => u.Id)
                                     .Where(u => u.HomeNodeId == nodeId)
                                     .ExecuteScalarAsync<long?>();
        if (userId == null)
            throw new NotFoundException<User>(nodeId);
        return userId.Value;
    }
}
