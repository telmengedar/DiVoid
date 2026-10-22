using System.Linq.Expressions;
using Backend.Models.Messages;
using Pooshit.Json;
using Pooshit.Ocelot.Info;
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
public class NodeService(IEntityManager database, IEmbeddingCapability embeddingCapability, IEmbeddingProducer embeddingProducer) : INodeService
{
    readonly IEntityManager database = database;
    readonly IEmbeddingCapability embeddingCapability = embeddingCapability;
    readonly IEmbeddingProducer embeddingProducer = embeddingProducer;

    const int MaxQueries = 10;

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
        using Transaction transaction = MutationTransaction();
        long[] existingTargets = node.Links?.Distinct().ToArray() ?? [];
        await LockNodes(transaction, existingTargets);
        Dictionary<long, Node> targetsBefore = [];
        foreach (long targetId in existingTargets) targetsBefore[targetId] = await Snapshot(transaction, targetId);
        long typeId = await ResolveOrCreateTypeId(node.Type, transaction);

        double insertX = node.X ?? 0.0;
        double insertY = node.Y ?? 0.0;
        DateTime now = DateTime.UtcNow;
        long nodeId = await database.Insert<Node>()
                              .Columns(n => n.TypeId, n => n.Name, n => n.Status, n => n.Severity, n => n.Refinement, n => n.RootNodeId, n => n.Substance, n => n.X, n => n.Y, n => n.OwnerId, n => n.Access, n => n.SkipEmbedding, n => n.Created, n => n.LastUpdate)
                              .Values(typeId, node.Name, node.Status, node.Severity, node.Refinement, node.RootNodeId, node.Substance, insertX, insertY, callerId, node.Access ?? (NodeAccess.Read | NodeAccess.Write), node.SkipEmbedding ?? false, now, now)
                              .ReturnID()
                              .ExecuteAsync(transaction);

        // create any requested links atomically with the node
        long[] links = node.Links?.Distinct().ToArray();
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

        if (embeddingCapability.IsEnabled && node.SkipEmbedding != true && !string.IsNullOrWhiteSpace(node.Name))
            await EmbeddingWrite.Build(database, nodeId, node.Name, null, null, embeddingProducer).ExecuteAsync(transaction);

        foreach (long targetId in existingTargets)
            await Notify(transaction, targetsBefore[targetId], await Snapshot(transaction, targetId), "Link added");
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

        using Transaction transaction = MutationTransaction();
        await LockNodes(transaction, nodeId);
        Node before = await Snapshot(transaction, nodeId);
        List<long> neighbors = [];
        await foreach (NodeLink link in database.Load<NodeLink>().Where(l => l.SourceId == nodeId || l.TargetId == nodeId).ExecuteEntitiesAsync(transaction))
            neighbors.Add(link.SourceId == nodeId ? link.TargetId : link.SourceId);
        long[] neighborIds = neighbors.Distinct().ToArray();
        await LockNodes(transaction, neighborIds);
        if (await database.Delete<Node>()
                         .Where(predicate.Content)
                         .ExecuteAsync(transaction) == 0)
            throw new NotFoundException<Node>(nodeId);
        await database.Delete<NodeLink>().Where(l => l.SourceId == nodeId || l.TargetId == nodeId).ExecuteAsync(transaction);
        await Notify(transaction, before, null);
        await database.Delete<NodeHook>().Where(h => h.NodeId == nodeId).ExecuteAsync(transaction);
        foreach (long neighborId in neighborIds) {
            Node neighbor = await Snapshot(transaction, neighborId);
            await Notify(transaction, neighbor, neighbor, "Link removed");
        }
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
    public async Task LinkNodes(long sourceNodeId, long targetNodeId, long callerId, bool isAdmin, LinkType linkType = LinkType.None, string context = null)
    {
        if (sourceNodeId == targetNodeId)
            throw new NotSupportedException("Unable to link node to itself");

        PredicateExpression<Node> gate = NodeAuthorization.BuildVisibilityPredicate(callerId, isAdmin, write: true);
        PredicateExpression<Node> sourcePredicate = new PredicateExpression<Node>(n => n.Id == sourceNodeId);
        if (gate != null)
            sourcePredicate &= gate;

        using Transaction transaction = MutationTransaction();
        await LockNodes(transaction, sourceNodeId, targetNodeId);
        Node sourceBefore = await Snapshot(transaction, sourceNodeId);
        Node targetBefore = await Snapshot(transaction, targetNodeId);
        if (await database.Load<Node>(DB.Count()).Where(sourcePredicate.Content).ExecuteScalarAsync<long>(transaction) == 0)
            throw new NotFoundException<Node>(sourceNodeId);

        if (await database.Load<Node>(DB.Count()).Where(n => n.Id == sourceNodeId || n.Id == targetNodeId).ExecuteScalarAsync<long>(transaction) != 2)
            throw new NotFoundException<Node>($"Either '{sourceNodeId}' or '{targetNodeId}' does not exist");
        if (await database.Load<NodeLink>(DB.Count()).Where(n => n.SourceId == sourceNodeId && n.TargetId == targetNodeId || n.SourceId == targetNodeId && n.TargetId == sourceNodeId).ExecuteScalarAsync<long>(transaction) > 0)
            return;
        await database.Insert<NodeLink>()
                      .Columns(n => n.SourceId, n => n.TargetId, n => n.LinkType, n => n.Context)
                      .Values(sourceNodeId, targetNodeId, linkType, context)
                      .ExecuteAsync(transaction);
        await TryAnchorOrphanToPositioned(transaction, sourceNodeId, [targetNodeId]);
        await TryAnchorOrphanToPositioned(transaction, targetNodeId, [sourceNodeId]);
        await Notify(transaction, sourceBefore, await Snapshot(transaction, sourceNodeId), "Link added");
        await Notify(transaction, targetBefore, await Snapshot(transaction, targetNodeId), "Link added");
        transaction.Commit();
    }

    /// <summary>
    /// builds a predicate for a single hop segment.
    /// Handles id, type, name, status, severity, refinement — with wildcard LIKE semantics on name/status/refinement.
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

                case "refinement":
                    if (p.Values.Any(v => v.ContainsWildcards()))
                    {
                        PredicateExpression<Node> refinementPredicate = null;
                        foreach (string refinementFilter in p.Values)
                            refinementPredicate |= n => n.Refinement.Like(refinementFilter);
                        predicate &= refinementPredicate;
                    } else
                    {
                        predicate &= n => n.Refinement.In(p.Values);
                    }
                    break;
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

        if (filter.ExcludeId?.Length > 0)
            predicate &= n => !n.Id.In(filter.ExcludeId);

        if (filter.ExcludeName?.Length > 0)
            if (filter.ExcludeName.Any(n => n.ContainsWildcards()))
            {
                foreach (string nameFilter in filter.ExcludeName)
                    predicate &= n => n.Name == null || !n.Name.Like(nameFilter);
            } else
            {
                predicate &= n => n.Name == null || !n.Name.In(filter.ExcludeName);
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

        if (filter.Refinement?.Length > 0)
        {
            PredicateExpression<Node> refinementValuePredicate;
            if (filter.Refinement.Any(r => r.ContainsWildcards()))
            {
                refinementValuePredicate = null;
                foreach (string refinementFilter in filter.Refinement)
                    refinementValuePredicate |= n => n.Refinement.Like(refinementFilter);
            } else
            {
                refinementValuePredicate = new PredicateExpression<Node>(n => n.Refinement.In(filter.Refinement));
            }

            if (filter.NoRefinement)
            {
                predicate &= refinementValuePredicate | new PredicateExpression<Node>(n => n.Refinement == null || n.Refinement == "");
            } else
            {
                predicate &= refinementValuePredicate;
            }
        } else if (filter.NoRefinement)
        {
            predicate &= n => n.Refinement == null || n.Refinement == "";
        }

        if (filter.RootNodeId?.Length > 0)
        {
            long[] rootIds = filter.RootNodeId;
            PredicateExpression<Node> rootNodeIdValuePredicate =
                new PredicateExpression<Node>(n => n.RootNodeId.In(rootIds));

            if (filter.NoRootNodeId)
            {
                predicate &= rootNodeIdValuePredicate | new PredicateExpression<Node>(n => n.RootNodeId == null);
            } else
            {
                predicate &= rootNodeIdValuePredicate;
            }
        } else if (filter.NoRootNodeId)
        {
            predicate &= n => n.RootNodeId == null;
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
    /// reduces the requested linkedto seeds to the ids the caller may read; an admin or auth-disabled caller gets them unchanged
    /// </summary>
    async Task<long[]> ReadableSeeds(long[] requested, long callerId, bool isAdmin, CancellationToken ct)
    {
        PredicateExpression<Node> gate = NodeAuthorization.BuildVisibilityPredicate(callerId, isAdmin, write: false);
        if (gate == null || requested == null || requested.Length == 0)
            return requested;

        PredicateExpression<Node> predicate = new(n => n.Id.In(requested));
        predicate &= gate;

        List<long> readable = [];
        await foreach (Node node in database.Load<Node>(n => n.Id)
                                            .Where(predicate.Content)
                                            .ExecuteEntitiesAsync()
                                            .WithCancellation(ct))
            readable.Add(node.Id);
        return [.. readable];
    }

    /// <summary>
    /// builds the linkedto predicate: nodes with at least one link to any id in
    /// <paramref name="linkedTo"/>, excluding the seeds themselves.
    /// </summary>
    PredicateExpression<Node> BuildLinkedToFilter(long[] linkedTo)
    {
        LoadOperation<NodeLink> linkOp = database.Load<NodeLink>(l => l.SourceId)
                                                 .Where(l => l.SourceId.In(linkedTo) || l.TargetId.In(linkedTo))
                                                 .Union(database.Load<NodeLink>(n => n.TargetId)
                                                                .Where(l => l.SourceId.In(linkedTo) || l.TargetId.In(linkedTo)));
        return new PredicateExpression<Node>(n => n.Id.In(linkOp) && !n.Id.In(linkedTo));
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
    /// chains the hops of a path query into a terminal operation; every hop is gated by the caller's read visibility
    /// </summary>
    ComposedPath ComposeHops(PathQuery query, NodePathFilter filter, long[] linkedToSeeds, NodeMapper mapper, bool isSemantic, long callerId, bool isAdmin, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        HopSegment[] hops = query.Hops;
        filter.Fields ??= mapper.DefaultListFields;

        PredicateExpression<Node> gate = NodeAuthorization.BuildVisibilityPredicate(callerId, isAdmin);

        PredicateExpression<Node> seedPredicate = null;
        Expression<Func<Node, bool>> seedPred = GenerateHopFilter(hops[0]);
        if (seedPred != null)
            seedPredicate &= new PredicateExpression<Node>(seedPred);
        if (gate != null)
            seedPredicate &= gate;

        LoadOperation<Node> currentHopOp = database.Load<Node>(n => n.Id);
        if (seedPredicate != null)
            currentHopOp.Where(seedPredicate.Content);

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
            if (gate != null)
                chainPredicate &= gate;

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
            combined &= BuildLinkedToFilter(linkedToSeeds);

        if (isSemantic)
            ApplySemanticSearch(terminal, filter, mapper, ref combined);

        terminal.Where(combined?.Content);

        return new(terminal);
    }

    /// <summary>
    /// ANDs semantic-search predicates into <paramref name="predicate"/> and overrides the
    /// ORDER BY on <paramref name="operation"/> when <paramref name="filter"/> carries a
    /// non-empty <c>Query</c> or <c>Queries</c>.
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
    /// When <c>Query</c> or <c>Queries</c> is present this method:
    /// <list type="bullet">
    ///   <item>ANDs <c>n.Embedding IS NOT NULL</c> into <paramref name="predicate"/> to exclude un-embedded nodes</item>
    ///   <item>ANDs <c>similarity &gt;= MinSimilarity</c> into <paramref name="predicate"/> when a floor is supplied; the floor reads the similarity column the mapper precomputes per row</item>
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
    /// throws <see cref="ArgumentException"/> (→ HTTP 400 via <c>ArgumentExceptionHandler</c>) when the array
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

    static void ValidateQueries(NodeFilter filter)
    {
        if (filter.Queries == null || filter.Queries.Length == 0)
            return;
        if (!string.IsNullOrWhiteSpace(filter.Query))
            throw new ArgumentException("supply either query or queries, not both", "queries");
        if (filter.Queries.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("queries must not contain blank values", "queries");
        if (filter.Queries.Length > MaxQueries)
            throw new ArgumentException($"at most {MaxQueries} queries are allowed", "queries");
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

    /// <summary>Single batched secondary query — never call per-row. Preserves the true stored
    /// source/target orientation (unlike <see cref="FetchAdjacentIds"/>, which collapses to
    /// symmetric neighbor ids).</summary>
    async Task<Dictionary<long, NodeLink[]>> FetchAdjacentEdges(long[] ids, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Dictionary<long, List<NodeLink>> adjacency = [];
        foreach (long id in ids)
            adjacency[id] = [];

        await foreach (NodeLink link in database.Load<NodeLink>(l => l.SourceId, l => l.TargetId, l => l.LinkType, l => l.Context)
                                                .Where(l => l.SourceId.In(ids) || l.TargetId.In(ids))
                                                .ExecuteEntitiesAsync())
        {
            if (link.SourceId == link.TargetId)
                continue;

            if (adjacency.TryGetValue(link.SourceId, out List<NodeLink> srcList))
                srcList.Add(link);
            if (adjacency.TryGetValue(link.TargetId, out List<NodeLink> tgtList))
                tgtList.Add(link);
        }

        Dictionary<long, NodeLink[]> result = [];
        foreach ((long id, List<NodeLink> edges) in adjacency)
            result[id] = [.. edges];
        return result;
    }

    /// <summary>
    /// materializes a page of rows and, per the requested flags, annotates each with inline
    /// neighbor ids (<see cref="NodeDetails.Links"/>) and/or inline edges
    /// (<see cref="NodeDetails.LinkDetails"/>). rows with no incident links/edges receive an
    /// empty array, not null. no-op pass-through when both flags are false.
    /// </summary>
    async Task<List<NodeDetails>> MaterializeAdjacency(IAsyncEnumerable<NodeDetails> items, bool includeLinks, bool includeLinkDetails, CancellationToken ct)
    {
        List<NodeDetails> rows = [];
        await foreach (NodeDetails node in items)
            rows.Add(node);

        if (rows.Count == 0)
            return rows;

        long[] ids = new long[rows.Count];
        for (int i = 0; i < rows.Count; i++)
            ids[i] = rows[i].Id;

        if (includeLinks)
        {
            Dictionary<long, long[]> adjacency = await FetchAdjacentIds(ids, ct);
            foreach (NodeDetails node in rows)
                node.Links = adjacency.TryGetValue(node.Id, out long[] neighbors) ? neighbors : [];
        }

        if (includeLinkDetails)
        {
            Dictionary<long, NodeLink[]> adjacency = await FetchAdjacentEdges(ids, ct);
            foreach (NodeDetails node in rows)
                node.LinkDetails = adjacency.TryGetValue(node.Id, out NodeLink[] edges) ? edges : [];
        }

        return rows;
    }

    /// <inheritdoc />
    public async Task<AsyncPageResponseWriter<NodeDetails>> ListPaged(NodeFilter filter, long callerId, bool isAdmin, CancellationToken ct = default)
    {
        filter ??= new();

        ValidateBounds(filter);
        ValidateQueries(filter);

        bool isSemantic = filter.GetEffectiveQueries().Length > 0;

        // guard: minSimilarity without query is a caller error
        if (!isSemantic && filter.MinSimilarity.HasValue)
            throw new SemanticSearchUnavailableException("minSimilarity requires query or queries");

        // guard: semantic search requires Postgres (the embedding() function)
        if (isSemantic && !embeddingCapability.IsEnabled)
            throw new SemanticSearchUnavailableException(
                "Semantic search requires Postgres; this deployment does not support the embedding function.");

        if (filter.Fields != null
            && filter.Fields.Contains("similarity", StringComparer.OrdinalIgnoreCase)
            && !isSemantic)
            throw new SemanticSearchUnavailableException(
                "Field 'similarity' is only available when a semantic query is provided via '?query=' or '?queries='.");

        if (string.Equals(filter.Sort, "content", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("sort=content is not supported");
        if (string.Equals(filter.Sort, "links", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("sort=links is not supported");
        if (string.Equals(filter.Sort, "linkDetails", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("sort=linkDetails is not supported");

        NodeMapper mapper = new(filter, embeddingProducer);
        filter.Fields ??= isSemantic
            ? [.. mapper.DefaultListFields, "similarity"]
            : mapper.DefaultListFields;

        if (filter.Fields.Contains("content") && !filter.Fields.Contains("contentType"))
            filter.Fields = [.. filter.Fields, "contentType"];

        // links / linkDetails are derived; not in mapper vocab.
        bool includeLinks = filter.Fields.Contains("links");
        bool includeLinkDetails = filter.Fields.Contains("linkDetails");
        if (includeLinks || includeLinkDetails)
            filter.Fields = filter.Fields.Where(f => !string.Equals(f, "links", StringComparison.OrdinalIgnoreCase)
                                                    && !string.Equals(f, "linkDetails", StringComparison.OrdinalIgnoreCase))
                                          .ToArray();

        LoadOperation<Node> operation = mapper.CreateOperation(database, filter.Fields);
        operation.ApplyFilter(filter, mapper);
        PredicateExpression<Node> listPredicate = null;
        Expression<Func<Node, bool>> generatedFilter = GenerateFilter(filter, callerId, isAdmin);
        if (generatedFilter != null)
            listPredicate &= new PredicateExpression<Node>(generatedFilter);
        if (filter.LinkedTo?.Length > 0)
            listPredicate &= BuildLinkedToFilter(await ReadableSeeds(filter.LinkedTo, callerId, isAdmin, ct));

        if (isSemantic)
            ApplySemanticSearch(operation, filter, mapper, ref listPredicate);

        operation.Where(listPredicate?.Content);

        WindowResult<NodeDetails, long> windowed =
            await mapper.WindowedFromOperation<long, Node>(operation, DB.CountOver(), ct, filter.Fields);

        if (includeLinks || includeLinkDetails)
        {
            List<NodeDetails> rows = await MaterializeAdjacency(windowed.Items, includeLinks, includeLinkDetails, ct);
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
        ValidateQueries(filter);

        bool isSemantic = filter.GetEffectiveQueries().Length > 0;

        // guard: minSimilarity without query is a caller error
        if (!isSemantic && filter.MinSimilarity.HasValue)
            throw new SemanticSearchUnavailableException("minSimilarity requires query or queries");

        // guard: semantic search requires Postgres (the embedding() function)
        if (isSemantic && !embeddingCapability.IsEnabled)
            throw new SemanticSearchUnavailableException(
                "Semantic search requires Postgres; this deployment does not support the embedding function.");

        if (string.Equals(filter.Sort, "content", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("sort=content is not supported");
        if (string.Equals(filter.Sort, "links", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("sort=links is not supported");
        if (string.Equals(filter.Sort, "linkDetails", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("sort=linkDetails is not supported");

        NodeMapper mapper = new(filter, embeddingProducer);
        filter.Fields ??= isSemantic
            ? [.. mapper.DefaultListFields, "similarity"]
            : mapper.DefaultListFields;

        if (filter.Fields.Contains("content") && !filter.Fields.Contains("contentType"))
            filter.Fields = [.. filter.Fields, "contentType"];

        // links / linkDetails are derived; not in mapper vocab.
        bool includeLinks = filter.Fields.Contains("links");
        bool includeLinkDetails = filter.Fields.Contains("linkDetails");
        if (includeLinks || includeLinkDetails)
            filter.Fields = filter.Fields.Where(f => !string.Equals(f, "links", StringComparison.OrdinalIgnoreCase)
                                                    && !string.Equals(f, "linkDetails", StringComparison.OrdinalIgnoreCase))
                                          .ToArray();

        // Parse throws PathQueryParseException on syntax/constraint violations (→ HTTP 400)
        PathQuery query = PathQueryParser.Parse(filter.Path);

        ct.ThrowIfCancellationRequested();

        long[] linkedToSeeds = await ReadableSeeds(filter.LinkedTo, callerId, isAdmin, ct);

        ComposedPath composed = ComposeHops(query, filter, linkedToSeeds, mapper, isSemantic, callerId, isAdmin, ct);

        ct.ThrowIfCancellationRequested();

        WindowResult<NodeDetails, long> windowed =
            await mapper.WindowedFromOperation<long, Node>(composed.Terminal, DB.CountOver(), ct, filter.Fields);

        if (includeLinks || includeLinkDetails)
        {
            List<NodeDetails> rows = await MaterializeAdjacency(windowed.Items, includeLinks, includeLinkDetails, ct);
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

        bool embeddingTouched = embeddingCapability.IsEnabled && (TouchesName(patches) || TouchesPath(patches, "/skipEmbedding"));
        using Transaction transaction = MutationTransaction();
        await LockNodes(transaction, nodeId);
        Node before = await Snapshot(transaction, nodeId);

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
        long touchAffected = await database.Update<Node>()
                                           .Set(n => n.LastUpdate == patchedAt)
                                           .Where(predicate.Content)
                                           .ExecuteAsync(transaction);
        if (touchAffected == 0)
            throw new NotFoundException<Node>(nodeId);

        if (embeddingTouched)
            await RegenerateEmbedding(transaction, nodeId, ct);

        await Notify(transaction, before, await Snapshot(transaction, nodeId));
        transaction.Commit();
        return await GetNodeById(nodeId, callerId, isAdmin: true);
    }

    async Task RegenerateEmbedding(Transaction transaction, long nodeId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Node row = await database.Load<Node>(n => n.Name, n => n.ContentType, n => n.SkipEmbedding)
                                 .Where(n => n.Id == nodeId)
                                 .ExecuteEntityAsync(transaction);

        if (row.SkipEmbedding)
        {
            await EmbeddingWrite.Build(database, nodeId, null, null, null, embeddingProducer).ExecuteAsync(transaction);
            return;
        }

        byte[] content = null;
        if (TextContentTypePredicate.IsText(row.ContentType))
        {
            Node contentRow = await database.Load<Node>(n => n.Content)
                                            .Where(n => n.Id == nodeId)
                                            .ExecuteEntityAsync(transaction);
            content = contentRow.Content;
        }

        ct.ThrowIfCancellationRequested();
        await EmbeddingWrite.Build(database, nodeId, row.Name, content, row.ContentType, embeddingProducer).ExecuteAsync(transaction);
    }

    /// <inheritdoc />
    public async Task UnlinkNodes(long sourceNodeId, long targetNodeId, long callerId, bool isAdmin)
    {
        PredicateExpression<Node> gate = NodeAuthorization.BuildVisibilityPredicate(callerId, isAdmin, write: true);
        PredicateExpression<Node> sourcePredicate = new PredicateExpression<Node>(n => n.Id == sourceNodeId);
        if (gate != null)
            sourcePredicate &= gate;

        using Transaction transaction = MutationTransaction();
        await LockNodes(transaction, sourceNodeId, targetNodeId);
        if (await database.Load<Node>(DB.Count()).Where(sourcePredicate.Content).ExecuteScalarAsync<long>(transaction) == 0)
            throw new NotFoundException<Node>(sourceNodeId);

        long removed = await database.Delete<NodeLink>()
                      .Where(n => n.SourceId == sourceNodeId && n.TargetId == targetNodeId || n.SourceId == targetNodeId && n.TargetId == sourceNodeId)
                      .ExecuteAsync(transaction);
        if (removed > 0) {
            Node source = await Snapshot(transaction, sourceNodeId);
            Node target = await Snapshot(transaction, targetNodeId);
            await Notify(transaction, source, source, "Link removed");
            await Notify(transaction, target, target, "Link removed");
        }
        transaction.Commit();
    }

    /// <inheritdoc />
    public async Task<NodeLink> PatchLink(long sourceNodeId, long targetNodeId, PatchOperation[] patches, long callerId, bool isAdmin, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        PredicateExpression<Node> gate = NodeAuthorization.BuildVisibilityPredicate(callerId, isAdmin, write: true);
        PredicateExpression<Node> sourcePredicate = new PredicateExpression<Node>(n => n.Id == sourceNodeId);
        if (gate != null)
            sourcePredicate &= gate;

        using Transaction transaction = MutationTransaction();
        await LockNodes(transaction, sourceNodeId, targetNodeId);
        if (await database.Load<Node>(DB.Count()).Where(sourcePredicate.Content).ExecuteScalarAsync<long>(transaction) == 0)
            throw new NotFoundException<Node>(sourceNodeId);

        Expression<Func<NodeLink, bool>> edgePredicate =
            l => l.SourceId == sourceNodeId && l.TargetId == targetNodeId || l.SourceId == targetNodeId && l.TargetId == sourceNodeId;

        NodeLink before = await database.Load<NodeLink>().Where(edgePredicate).ExecuteEntityAsync(transaction);
        if (await database.Update<NodeLink>()
                         .Patch(patches)
                         .Where(edgePredicate)
                         .ExecuteAsync(transaction) == 0)
            throw new NotFoundException<NodeLink>($"No link between '{sourceNodeId}' and '{targetNodeId}'");

        NodeLink after = await database.Load<NodeLink>(l => l.SourceId, l => l.TargetId, l => l.LinkType, l => l.Context)
                             .Where(edgePredicate)
                             .ExecuteEntityAsync(transaction);
        if (before.LinkType != after.LinkType || before.Context != after.Context) {
            Node source = await Snapshot(transaction, sourceNodeId);
            Node target = await Snapshot(transaction, targetNodeId);
            await Notify(transaction, source, source, "Link changed");
            await Notify(transaction, target, target, "Link changed");
        }
        transaction.Commit();
        return after;
    }

    /// <inheritdoc />
    public async Task UploadContent(long nodeId, string contentType, Stream data, long callerId, bool isAdmin, CancellationToken ct = default)
    {
        byte[] blob = await data.ToByteArray();

        PredicateExpression<Node> gate = NodeAuthorization.BuildVisibilityPredicate(callerId, isAdmin, write: true);
        PredicateExpression<Node> predicate = new PredicateExpression<Node>(n => n.Id == nodeId);
        if (gate != null)
            predicate &= gate;

        using Transaction transaction = MutationTransaction();
        await LockNodes(transaction, nodeId);
        Node before = await Snapshot(transaction, nodeId, includeContent: true);

        DateTime uploadedAt = DateTime.UtcNow;
        if (await database.Update<Node>()
                          .Set(n => n.ContentType == contentType, n => n.Content == blob, n => n.Substance == (string) null, n => n.LastUpdate == uploadedAt)
                          .Where(predicate.Content)
                          .ExecuteAsync(transaction) == 0)
            throw new NotFoundException<Node>(nodeId);

        await RegenerateContentEmbedding(transaction, nodeId, blob, contentType, ct);

        await Notify(transaction, before, await Snapshot(transaction, nodeId, includeContent: true));
        transaction.Commit();
    }

    async Task RegenerateContentEmbedding(Transaction transaction, long nodeId, byte[] content, string contentType, CancellationToken ct)
    {
        if (!embeddingCapability.IsEnabled)
            return;

        ct.ThrowIfCancellationRequested();
        Node row = await database.Load<Node>(n => n.Name, n => n.SkipEmbedding)
                                 .Where(n => n.Id == nodeId)
                                 .ExecuteEntityAsync(transaction);
        if (row.SkipEmbedding)
            return;

        await EmbeddingWrite.Build(database, nodeId, row.Name, content, contentType, embeddingProducer).ExecuteAsync(transaction);
    }

    /// <inheritdoc />
    public async Task<NodeDetails> PatchContent(long nodeId, ContentEdit[] edits, long callerId, bool isAdmin, CancellationToken ct)
    {
        PredicateExpression<Node> gate = NodeAuthorization.BuildVisibilityPredicate(callerId, isAdmin, write: true);
        PredicateExpression<Node> predicate = new PredicateExpression<Node>(n => n.Id == nodeId);
        if (gate != null)
            predicate &= gate;

        using Transaction transaction = MutationTransaction();
        await LockNodes(transaction, nodeId);
        Node before = await Snapshot(transaction, nodeId, includeContent: true);

        Node node = await database.Load<Node>(n => n.ContentType, n => n.Content)
                                  .Where(predicate.Content)
                                  .ExecuteEntityAsync(transaction);
        if (node == null)
            throw new NotFoundException<Node>(nodeId);
        if (node.Content == null)
            throw new NotFoundException<Node>($"Node '{nodeId}' has no content");

        byte[] edited = ContentEditor.Apply(node.Content, node.ContentType, edits);

        DateTime editedAt = DateTime.UtcNow;
        await database.Update<Node>()
                      .Set(n => n.Content == edited, n => n.Substance == (string) null, n => n.LastUpdate == editedAt)
                      .Where(n => n.Id == nodeId)
                      .ExecuteAsync(transaction);

        await RegenerateContentEmbedding(transaction, nodeId, edited, node.ContentType, ct);

        await Notify(transaction, before, await Snapshot(transaction, nodeId, includeContent: true));
        transaction.Commit();
        return await GetNodeById(nodeId, callerId, isAdmin: true);
    }

    /// <inheritdoc />
    public Task<AsyncPageResponseWriter<NodeLink>> ListLinks(long[] ids, ListFilter filter, CancellationToken ct)
    {
        filter ??= new();
        if (filter.Count is null or > 500)
            filter.Count = 500;

        LoadOperation<NodeLink> operation = database.Load<NodeLink>(l => l.SourceId, l => l.TargetId, l => l.LinkType, l => l.Context)
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

    Transaction MutationTransaction() {
        Transaction transaction = database.Transaction();
        try {
            if (database.DBClient.DBInfo is PostgreInfo)
                database.DBClient.NonQuery(transaction, "SET TRANSACTION ISOLATION LEVEL SERIALIZABLE");
            return transaction;
        }
        catch {
            transaction.Dispose();
            throw;
        }
    }


    async Task LockNodes(Transaction transaction, params long[] ids) {
        foreach (long id in ids.Distinct().Order())
            await database.Update<Node>().Set(n => n.LastUpdate == n.LastUpdate)
                          .Where(n => n.Id == id).ExecuteAsync(transaction);
    }


    Task<Node> Snapshot(Transaction transaction, long id, bool includeContent = false) {
        LoadOperation<Node> operation = database.Load<Node>(
            n => n.Id, n => n.TypeId, n => n.Name, n => n.Status, n => n.Severity,
            n => n.Refinement, n => n.RootNodeId, n => n.Substance, n => n.SkipEmbedding,
            n => n.OwnerId, n => n.Access, n => n.X, n => n.Y, n => n.ContentType);
        if (includeContent) operation.Fields(n => n.Content);
        return operation.Where(n => n.Id == id).ExecuteEntityAsync(transaction);
    }


    static bool CanRead(Node node, User user) => node != null && user != null && user.Enabled &&
        (node.OwnerId == user.Id || (node.Access & NodeAccess.Read) != 0 ||
         (!string.IsNullOrEmpty(user.Permissions) && (Json.Read<string[]>(user.Permissions) ?? []).Contains("admin")));


    static string StatusText(string status) => status == null ? "(unset)" : status.Length > 128 ? status[..127] + "…" : status;


    static List<string> Changes(Node before, Node after) {
        List<string> changes = [];
        if (after == null) return [$"Node #{before.Id} deleted"];
        if (before.Status != after.Status) changes.Add($"Status changed: {StatusText(before.Status)} → {StatusText(after.Status)}");
        if (before.Name != after.Name) changes.Add("Name changed");
        if (before.TypeId != after.TypeId) changes.Add("Type changed");
        if (before.Severity != after.Severity) changes.Add("Severity changed");
        if (before.Refinement != after.Refinement) changes.Add("Refinement changed");
        if (before.RootNodeId != after.RootNodeId) changes.Add("Root node changed");
        if (before.Substance != after.Substance) changes.Add("Substance changed");
        if (before.SkipEmbedding != after.SkipEmbedding) changes.Add("Embedding preference changed");
        if (before.OwnerId != after.OwnerId) changes.Add("Owner changed");
        if (before.Access != after.Access) changes.Add("Access changed");
        if (before.X != after.X || before.Y != after.Y) changes.Add("Position changed");
        if (before.ContentType != after.ContentType || !(before.Content ?? []).SequenceEqual(after.Content ?? []) ||
            (before.Content == null) != (after.Content == null)) changes.Add("Content changed");
        return changes;
    }


    async Task Notify(Transaction transaction, Node before, Node after, string edgeChange = null) {
        if (before == null) return;
        List<string> changes = Changes(before, after);
        if (edgeChange != null) changes.Add(edgeChange);
        if (changes.Count == 0) return;
        long nodeId = before.Id;
        List<NodeHook> hooks = [];
        await foreach (NodeHook hook in database.Load<NodeHook>().Where(h => h.NodeId == nodeId).ExecuteEntitiesAsync(transaction))
            hooks.Add(hook);
        foreach (NodeHook hook in hooks) {
            User user = await database.Load<User>(u => u.Id, u => u.Enabled, u => u.Permissions)
                                      .Where(u => u.Id == hook.UserId).ExecuteEntityAsync(transaction);
            bool readableBefore = CanRead(before, user);
            bool readableAfter = CanRead(after, user);
            if (!readableBefore && !readableAfter) continue;
            string body = after != null && !readableAfter
                ? $"Access changed for node #{nodeId}."
                : $"Node #{nodeId}:\n" + string.Join("\n", changes);
            await database.Insert<Message>()
                          .Columns(m => m.AuthorId, m => m.RecipientId, m => m.Subject, m => m.Body, m => m.CreatedAt)
                          .Values(0L, hook.UserId, $"[{hook.Tag}] Node #{nodeId} changed", body, DateTime.UtcNow)
                          .ExecuteAsync(transaction);
        }
    }


    static void ValidateHook(NodeHook hook, long callerId, bool isAdmin) {
        if (hook == null || hook.NodeId <= 0 || hook.UserId <= 0)
            throw new ArgumentException("Positive nodeId and userId are required");
        if (!isAdmin && hook.UserId != callerId) throw new NotFoundException<NodeHook>(hook.NodeId);
        hook.Tag = hook.Tag?.Trim();
        if (string.IsNullOrEmpty(hook.Tag) || hook.Tag.Length > 128 || hook.Tag.IndexOfAny(['[', ']', '\r', '\n']) >= 0)
            throw new ArgumentException("tag must contain 1–128 characters without brackets or newlines");
    }


    /// <inheritdoc />
    public async Task<NodeHook> RegisterHook(NodeHook hook, long callerId, bool isAdmin) {
        ValidateHook(hook, callerId, isAdmin);
        using Transaction transaction = MutationTransaction();
        await LockNodes(transaction, hook.NodeId);
        Node node = await Snapshot(transaction, hook.NodeId);
        User recipient = await database.Load<User>(u => u.Id, u => u.Enabled, u => u.Permissions)
                                       .Where(u => u.Id == hook.UserId).ExecuteEntityAsync(transaction);
        if (!CanRead(node, recipient)) throw new NotFoundException<Node>(hook.NodeId);
        if (await database.Load<NodeHook>(DB.Count()).Where(h => h.NodeId == hook.NodeId && h.UserId == hook.UserId && h.Tag == hook.Tag)
                          .ExecuteScalarAsync<long>(transaction) == 0)
            await database.Insert<NodeHook>().Columns(h => h.NodeId, h => h.UserId, h => h.Tag)
                          .Values(hook.NodeId, hook.UserId, hook.Tag).ExecuteAsync(transaction);
        transaction.Commit();
        return hook;
    }


    /// <inheritdoc />
    public async Task<NodeHook[]> ListHooks(long? nodeId, long? userId, long callerId, bool isAdmin) {
        long recipientId = userId ?? callerId;
        if (nodeId <= 0 || recipientId <= 0) throw new ArgumentException("Ids must be positive");
        if (!isAdmin && recipientId != callerId) throw new NotFoundException<NodeHook>(recipientId);
        PredicateExpression<NodeHook> predicate = new(h => h.UserId == recipientId);
        if (nodeId.HasValue) predicate &= h => h.NodeId == nodeId.Value;
        List<NodeHook> hooks = [];
        await foreach (NodeHook hook in database.Load<NodeHook>().Where(predicate.Content).ExecuteEntitiesAsync()) hooks.Add(hook);
        return hooks.ToArray();
    }


    /// <inheritdoc />
    public async Task DeleteHook(NodeHook hook, long callerId, bool isAdmin) {
        ValidateHook(hook, callerId, isAdmin);
        using Transaction transaction = MutationTransaction();
        await LockNodes(transaction, hook.NodeId);
        await database.Delete<NodeHook>().Where(h => h.NodeId == hook.NodeId && h.UserId == hook.UserId && h.Tag == hook.Tag)
                      .ExecuteAsync(transaction);
        transaction.Commit();
    }


    /// <inheritdoc />
    public async Task PositionUnpositionedNode(long nodeId, double x, double y, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        using Transaction transaction = MutationTransaction();
        await LockNodes(transaction, nodeId);
        Node before = await Snapshot(transaction, nodeId);
        if (before == null || before.X != 0 || before.Y != 0) return;
        await database.Update<Node>().Set(n => n.X == x, n => n.Y == y).Where(n => n.Id == nodeId).ExecuteAsync(transaction);
        await Notify(transaction, before, await Snapshot(transaction, nodeId));
        transaction.Commit();
    }

}
