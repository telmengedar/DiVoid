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
    /// </summary>
    ComposedPath ComposeHops(PathQuery query, NodePathFilter filter, NodeMapper mapper, CancellationToken ct)
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

        LoadOperation<Node> terminal = mapper.CreateOperation(database, filter.Fields);
        terminal.ApplyFilter(filter, mapper, ignoreLimits: true);
        terminal.Where(terminalPredicate);

        return new(terminal);
    }

    /// <inheritdoc />
    public async Task<AsyncPageResponseWriter<NodeDetails>> ListPaged(NodeFilter filter = null)
    {
        filter ??= new();
        NodeMapper mapper = new();
        filter.Fields ??= mapper.DefaultListFields;

        // Clamp count and resolve paging params before the single-query execution
        if (filter.Count is null or > 500)
            filter.Count = 500;

        int limit = (int)filter.Count!.Value;
        int offset = (int)(filter.Continue ?? 0L);

        // Apply sort only — limit/offset are passed directly to ExecutePagedAsync
        LoadOperation<Node> operation = mapper.CreateOperation(database, filter.Fields);
        operation.ApplyFilter(filter, mapper, ignoreLimits: true);

        Expression<Func<Node, bool>> predicate = GenerateFilter(filter);
        operation.Where(predicate);

        // Single query: COUNT(*) OVER () window function supplies total alongside page rows
        Pooshit.Ocelot.Entities.Operations.Prepared.PagedResult<Node> paged =
            await operation.ExecutePagedAsync(limit, offset, CancellationToken.None);

        return new AsyncPageResponseWriter<NodeDetails>(
            mapper.EntitiesFromPaged(database, paged.Items, filter.Fields),
            () => paged.Total,
            filter.Continue
        );
    }

    /// <inheritdoc />
    public async Task<AsyncPageResponseWriter<NodeDetails>> ListPagedByPath(NodePathFilter filter, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        filter ??= new();
        NodeMapper mapper = new();
        filter.Fields ??= mapper.DefaultListFields;

        // Parse throws PathQueryParseException on syntax/constraint violations (→ HTTP 400)
        PathQuery query = PathQueryParser.Parse(filter.Path);

        ct.ThrowIfCancellationRequested();

        // Clamp count and resolve paging params before the single-query execution
        if (filter.Count is null or > 500)
            filter.Count = 500;

        int limit = (int)filter.Count!.Value;
        int offset = (int)(filter.Continue ?? 0L);

        ComposedPath composed = ComposeHops(query, filter, mapper, ct);

        ct.ThrowIfCancellationRequested();

        // Single query: COUNT(*) OVER () window function supplies total alongside page rows
        Pooshit.Ocelot.Entities.Operations.Prepared.PagedResult<Node> paged =
            await composed.Terminal.ExecutePagedAsync(limit, offset, ct);

        return new AsyncPageResponseWriter<NodeDetails>(
            mapper.EntitiesFromPaged(database, paged.Items, filter.Fields, ct),
            () => paged.Total,
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
