using System.Linq.Expressions;
using Backend.Extensions;
using Backend.Models.Nodes;
using Backend.Query;
using Pooshit.AspNetCore.Services.Errors.Exceptions;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Expressions;
using Pooshit.Ocelot.Fields;
using Pooshit.Ocelot.Tokens;

namespace Backend.Services.Nodes;

/// <inheritdoc />
public class NodeService(IEntityManager database) : INodeService
{
    readonly IEntityManager database = database;

    /// <inheritdoc />
    public async Task<NodeDetails> CreateNode(NodeDetails node)
    {
        using Transaction transaction = database.Transaction();
        NodeType type = await database.Load<NodeType>()
                                    .Where(t => t.Type == node.Type)
                                    .ExecuteEntityAsync(transaction);
        if (type == null)
            type = new()
            {
                Id = await database.Insert<NodeType>()
                                 .Columns(n => n.Type)
                                 .Values(node.Type)
                                 .ReturnID()
                                 .ExecuteAsync(transaction),
                Type = node.Type
            };

        node.Id = await database.Insert<Node>()
                              .Columns(n => n.TypeId, n => n.Name)
                              .Values(type.Id, node.Name)
                              .ReturnID()
                              .ExecuteAsync(transaction);
        transaction.Commit();
        return node;
    }

    /// <inheritdoc />
    public async Task Delete(long nodeId)
    {
        using Transaction transaction = database.Transaction();
        if (await database.Delete<Node>()
                         .Where(n => n.Id == nodeId)
                         .ExecuteAsync(transaction) == 0)
            throw new NotFoundException<Node>(nodeId);
        await database.Delete<NodeLink>().Where(l => l.SourceId == nodeId || l.TargetId == nodeId).ExecuteAsync(transaction);
        transaction.Commit();
    }

    /// <inheritdoc />
    public async Task<(string, Stream)> GetNodeData(long nodeId)
    {
        Node node = await database.Load<Node>(n => n.ContentType, n => n.Content)
                                .Where(n => n.Id == nodeId)
                                .ExecuteEntityAsync();
        if (node == null)
            throw new NotFoundException<Node>(nodeId);

        return (node.ContentType, new MemoryStream(node.Content));
    }

    /// <inheritdoc />
    public async Task LinkNodes(long sourceNodeId, long targetNodeId)
    {
        if (sourceNodeId == targetNodeId)
            throw new InvalidOperationException("Unable to link node to itself");
        using Transaction transaction = database.Transaction();
        if (await database.Load<Node>(DB.Count()).Where(n => n.Id == sourceNodeId || n.Id == targetNodeId).ExecuteScalarAsync<long>(transaction) != 2)
            throw new NotFoundException<Node>($"Either '{sourceNodeId}' or '{targetNodeId}' does not exist");
        if (await database.Load<NodeLink>(DB.Count()).Where(n => n.SourceId == sourceNodeId && n.TargetId == targetNodeId || n.SourceId == targetNodeId && n.TargetId == sourceNodeId).ExecuteScalarAsync<long>(transaction) > 0)
            throw new InvalidOperationException("Nodes already linked");
        await database.Insert<NodeLink>()
                      .Columns(n => n.SourceId, n => n.TargetId)
                      .Values(sourceNodeId, targetNodeId)
                      .ExecuteAsync(transaction);
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
            }
        }

        return predicate?.Content;
    }

    /// <summary>
    /// original single-filter method — preserved for the existing list endpoint.
    /// </summary>
    Expression<Func<Node, bool>> GenerateFilter(NodeFilter filter)
    {
        PredicateExpression<Node> predicate = null;

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
            predicate &= n => n.TypeId.In(typeOp);
        }

        if (filter.LinkedTo?.Length > 0)
        {
            // TODO: Lateral Join in Ocelot
            LoadOperation<NodeLink> linkOp = database.Load<NodeLink>(l => l.SourceId)
                                                   .Where(l => l.SourceId.In(filter.LinkedTo) || l.TargetId.In(filter.LinkedTo))
                                                   .Union(database.Load<NodeLink>(n => n.TargetId).Where(l => l.SourceId.In(filter.LinkedTo) || l.TargetId.In(filter.LinkedTo)));
            predicate &= n => n.Id.In(linkOp) && !n.Id.In(filter.LinkedTo);
        }

        if (filter.Status?.Length > 0)
            if (filter.Status.Any(s => s.ContainsWildcards()))
            {
                PredicateExpression<Node> statusPredicate = null;
                foreach (string statusFilter in filter.Status)
                    statusPredicate |= n => n.Status.Like(statusFilter);
                predicate &= statusPredicate;
            } else
            {
                predicate &= n => n.Status.In(filter.Status);
            }

        if (filter.NoStatus)
            predicate &= n => n.Status == null || n.Status == "";

        return predicate?.Content;
    }

    /// <summary>
    /// builds the link sub-query for both directions of <see cref="NodeLink"/> from
    /// <paramref name="prevHopIds"/>.  This is the same Union-of-two-directions pattern
    /// used by the original <c>linkedto</c> filter.
    /// </summary>
    LoadOperation<NodeLink> BuildLinkSubquery(long[] prevHopIds)
    {
        return database.Load<NodeLink>(l => l.SourceId)
                       .Where(l => l.SourceId.In(prevHopIds) || l.TargetId.In(prevHopIds))
                       .Union(database.Load<NodeLink>(l => l.TargetId)
                                      .Where(l => l.SourceId.In(prevHopIds) || l.TargetId.In(prevHopIds)));
    }

    /// <summary>
    /// resolves the first (seed) hop to a set of node ids.
    /// No traversal — plain filter on Node.
    /// </summary>
    async Task<long[]> ResolveSeedHop(HopSegment hop, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var seedOp = database.Load<Node>(n => n.Id);
        if (hop.Predicates.Length > 0)
        {
            Expression<Func<Node, bool>> pred = GenerateHopFilter(hop);
            if (pred != null)
                seedOp.Where(pred);
        }
        // If empty (any-node wildcard as seed), load all ids — valid but expensive

        var result = new List<long>();
        await foreach (Node n in seedOp.ExecuteEntitiesAsync())
        {
            ct.ThrowIfCancellationRequested();
            result.Add(n.Id);
        }
        return result.ToArray();
    }

    /// <summary>
    /// resolves an intermediate hop by following links from <paramref name="prevIds"/>
    /// and filtering the reachable nodes by <paramref name="hop"/>'s predicates.
    /// Returns the resulting id-set.
    /// </summary>
    async Task<long[]> ResolveIntermediateHop(HopSegment hop, long[] prevIds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (prevIds.Length == 0)
            return [];

        LoadOperation<NodeLink> linkOp = BuildLinkSubquery(prevIds);
        PredicateExpression<Node> chainPredicate = null;
        chainPredicate &= n => n.Id.In(linkOp) && !n.Id.In(prevIds);

        Expression<Func<Node, bool>> hopPred = GenerateHopFilter(hop);
        if (hopPred != null)
            chainPredicate &= new PredicateExpression<Node>(hopPred);

        var nodeOp = database.Load<Node>(n => n.Id).Where(chainPredicate.Content);

        var result = new List<long>();
        await foreach (Node n in nodeOp.ExecuteEntitiesAsync())
        {
            ct.ThrowIfCancellationRequested();
            result.Add(n.Id);
        }
        return result.ToArray();
    }

    /// <summary>
    /// result of composing hops: the terminal operation (for streaming results) and the
    /// terminal predicate (for the count query, built separately to avoid re-traversal).
    /// </summary>
    readonly record struct ComposedPath(
        LoadOperation<Node> Terminal,
        Expression<Func<Node, bool>> TerminalPredicate
    );

    /// <summary>
    /// chains N hops into a terminal <see cref="LoadOperation{Node}"/> plus a raw predicate
    /// expression for the count query.
    ///
    /// For a 1-hop path this is a plain per-hop filter with no traversal.
    /// For N &gt; 1 hops, hops 1..N-1 are materialised as id-sets; hop N receives
    /// a link-subquery constraint that chains it to the previous hop.
    /// </summary>
    async Task<ComposedPath> ComposeHops(PathQuery query, NodePathFilter filter, NodeMapper mapper, CancellationToken ct)
    {
        HopSegment[] hops = query.Hops;
        filter.Fields ??= mapper.DefaultListFields;

        if (hops.Length == 1)
        {
            // Single-hop: plain filter, no traversal
            LoadOperation<Node> op = mapper.CreateOperation(database, filter.Fields);
            op.ApplyFilter(filter, mapper);
            Expression<Func<Node, bool>> singlePred = GenerateHopFilter(hops[0]);
            op.Where(singlePred);
            return new(op, singlePred);
        }

        // Multi-hop: resolve intermediate hops to id-sets
        ct.ThrowIfCancellationRequested();
        long[] currentIds = await ResolveSeedHop(hops[0], ct);

        for (int i = 1; i < hops.Length - 1; i++)
        {
            ct.ThrowIfCancellationRequested();
            currentIds = await ResolveIntermediateHop(hops[i], currentIds, ct);
        }

        ct.ThrowIfCancellationRequested();

        // Build terminal hop predicate
        Expression<Func<Node, bool>> terminalPred;
        if (currentIds.Length == 0)
        {
            // No intermediate results — terminal is definitively empty
            terminalPred = n => n.Id == -1;
        }
        else
        {
            LoadOperation<NodeLink> linkOp = BuildLinkSubquery(currentIds);
            PredicateExpression<Node> chainPred = null;
            chainPred &= n => n.Id.In(linkOp) && !n.Id.In(currentIds);

            Expression<Func<Node, bool>> hopPred = GenerateHopFilter(hops[hops.Length - 1]);
            if (hopPred != null)
                chainPred &= new PredicateExpression<Node>(hopPred);

            terminalPred = chainPred.Content;
        }

        LoadOperation<Node> terminal = mapper.CreateOperation(database, filter.Fields);
        terminal.ApplyFilter(filter, mapper);
        terminal.Where(terminalPred);

        return new(terminal, terminalPred);
    }

    /// <inheritdoc />
    public async Task<AsyncPageResponseWriter<NodeDetails>> ListPaged(NodeFilter filter = null)
    {
        filter ??= new();
        NodeMapper mapper = new();
        filter.Fields ??= mapper.DefaultListFields;

        LoadOperation<Node> operation = mapper.CreateOperation(database, filter.Fields);
        operation.ApplyFilter(filter, mapper);

        Expression<Func<Node, bool>> predicate = GenerateFilter(filter);
        operation.Where(predicate);

        if (filter.NoTotal)
        {
            // -1 signals to callers that the total was not computed.
            // We cannot pass null — AsyncPageResponseWriter requires a delegate.
            return new AsyncPageResponseWriter<NodeDetails>(
                mapper.EntitiesFromOperation(operation, filter.Fields),
                () => Task.FromResult(-1L),
                filter.Continue
            );
        }

        return new AsyncPageResponseWriter<NodeDetails>(
            mapper.EntitiesFromOperation(operation, filter.Fields),
            () => database.Load<Node>(DB.Count()).Where(predicate).ExecuteScalarAsync<long>(),
            filter.Continue
        );
    }

    /// <inheritdoc />
    public async Task<AsyncPageResponseWriter<NodeDetails>> ListPagedByPath(NodePathFilter filter, CancellationToken ct)
    {
        filter ??= new();
        NodeMapper mapper = new();
        filter.Fields ??= mapper.DefaultListFields;

        // Parse throws PathQueryParseException on syntax/constraint violations (→ HTTP 400)
        PathQuery query = PathQueryParser.Parse(filter.Path);

        ct.ThrowIfCancellationRequested();

        ComposedPath composed = await ComposeHops(query, filter, mapper, ct);

        if (filter.NoTotal)
        {
            // -1 signals that total was not computed (cannot pass null to AsyncPageResponseWriter)
            return new AsyncPageResponseWriter<NodeDetails>(
                mapper.EntitiesFromOperation(composed.Terminal, filter.Fields),
                () => Task.FromResult(-1L),
                filter.Continue
            );
        }

        // Capture predicate for count — built once, reused in the lambda
        Expression<Func<Node, bool>> countPred = composed.TerminalPredicate;

        return new AsyncPageResponseWriter<NodeDetails>(
            mapper.EntitiesFromOperation(composed.Terminal, filter.Fields),
            () => {
                ct.ThrowIfCancellationRequested();
                return database.Load<Node>(DB.Count()).Where(countPred).ExecuteScalarAsync<long>();
            },
            filter.Continue
        );
    }

    /// <inheritdoc />
    public async Task<NodeDetails> Patch(long nodeId, params PatchOperation[] patches)
    {
        if (await database.Update<Node>()
                         .Patch(patches)
                         .Where(n => n.Id == nodeId)
                         .ExecuteAsync() == 0)
            throw new NotFoundException<Node>(nodeId);
        return await GetNodeById(nodeId);
    }

    /// <inheritdoc />
    public async Task UnlinkNodes(long sourceNodeId, long targetNodeId)
    {
        if (await database.Delete<NodeLink>()
                          .Where(n => n.SourceId == sourceNodeId && n.TargetId == targetNodeId || n.SourceId == targetNodeId && n.TargetId == sourceNodeId)
                          .ExecuteAsync() == 0)
            throw new NotFoundException<NodeLink>($"No link found between nodes '{sourceNodeId}' and '{targetNodeId}'");
    }

    /// <inheritdoc />
    public async Task UploadContent(long nodeId, string contentType, Stream data)
    {
        byte[] blob = await data.ToByteArray();
        if (await database.Update<Node>()
                   .Set(n => n.ContentType == contentType, n => n.Content == blob)
                   .Where(n => n.Id == nodeId)
                   .ExecuteAsync() == 0)
            throw new NotFoundException<Node>(nodeId);
    }

    /// <inheritdoc />
    public Task<NodeDetails> GetNodeById(long nodeId)
    {
        NodeMapper mapper = new();
        LoadOperation<Node> operation = mapper.CreateOperation(database);
        operation.Where(n => n.Id == nodeId);
        return mapper.EntityFromOperation(operation);
    }
}
