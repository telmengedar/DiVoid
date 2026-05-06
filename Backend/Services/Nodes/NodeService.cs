using System.Linq.Expressions;
using Backend.Extensions;
using Backend.Models.Nodes;
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
        await database.Delete<NodeLink>().Where(l => l.SourceId == nodeId && l.TargetId == nodeId).ExecuteAsync(transaction);
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

        return predicate?.Content;
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

        return new AsyncPageResponseWriter<NodeDetails>(
            mapper.EntitiesFromOperation(operation, filter.Fields),
            () => database.Load<Node>(DB.Count()).Where(predicate).ExecuteScalarAsync<long>(),
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
