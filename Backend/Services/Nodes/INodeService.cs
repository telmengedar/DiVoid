using System.Threading;
using Backend.Models.Nodes;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.AspNetCore.Services.Patches;

namespace Backend.Services.Nodes;

/// <summary>
/// interface for service managing nodes
/// </summary>
public interface INodeService
{

    /// <summary>
    /// creates a new <see cref="Node"/>
    /// </summary>
    /// <param name="node">node to create</param>
    /// <returns>created node</returns>
    Task<NodeDetails> CreateNode(NodeDetails node);

    /// <summary>
    /// get a node by id
    /// </summary>
    /// <param name="nodeId">id of node to get</param>
    /// <returns><see cref="NodeDetails"/></returns>
    Task<NodeDetails> GetNodeById(long nodeId);

    /// <summary>
    /// lists existing nodes
    /// </summary>
    /// <param name="filter">filter to apply</param>
    /// <param name="ct">cancellation token bound to the HTTP request lifetime</param>
    /// <returns>page of nodes matching filter</returns>
    Task<AsyncPageResponseWriter<NodeDetails>> ListPaged(NodeFilter filter = null, CancellationToken ct = default);

    /// <summary>
    /// lists nodes reachable via a graph path expression
    /// </summary>
    /// <param name="filter">path filter with the raw path string and paging parameters</param>
    /// <param name="ct">cancellation token from the HTTP request</param>
    /// <returns>page of terminal-hop nodes</returns>
    Task<AsyncPageResponseWriter<NodeDetails>> ListPagedByPath(NodePathFilter filter, CancellationToken ct);

    /// <summary>
    /// get data of a node
    /// </summary>
    /// <param name="nodeId">id of node</param>
    /// <returns>content type and data of node</returns>
    Task<(string, Stream)> GetNodeData(long nodeId);

    /// <summary>
    /// patches data of a node.
    /// on Postgres, a patch that touches <c>/name</c> also regenerates the node's embedding
    /// inside the same transaction (new name + current content composition).
    /// </summary>
    /// <param name="nodeId">id of node to patch</param>
    /// <param name="patches">patches to apply</param>
    /// <param name="ct">cancellation token</param>
    /// <returns>patched node</returns>
    Task<NodeDetails> Patch(long nodeId, PatchOperation[] patches, CancellationToken ct);

    /// <summary>
    /// uploads data for a node.
    /// on Postgres: also (re)generates a vector embedding from the node's name plus the
    /// new content (or name alone if content is non-text/empty; null only if both are empty).
    /// on SQLite: content is written; embedding is not touched.
    /// </summary>
    /// <param name="nodeId">id of node for which to upload data</param>
    /// <param name="contentType">content type of data</param>
    /// <param name="data">data to upload</param>
    /// <param name="ct">cancellation token</param>
    Task UploadContent(long nodeId, string contentType, Stream data, CancellationToken ct = default);

    /// <summary>
    /// lists link adjacency rows where either endpoint is in <paramref name="ids"/>.
    /// used by the workspace viewport to fetch edges incident to visible nodes in a single round-trip.
    /// </summary>
    /// <param name="ids">node ids whose incident links are requested</param>
    /// <param name="filter">paging/sort filter</param>
    /// <param name="ct">cancellation token</param>
    /// <returns>page of link adjacency pairs</returns>
    Task<AsyncPageResponseWriter<NodeLink>> ListLinks(long[] ids, ListFilter filter, CancellationToken ct);

    /// <summary>
    /// lists all node types that are currently in use (i.e. have at least one referencing node),
    /// ordered by count descending then type name ascending.
    /// returns an empty result for a graph with no nodes.
    /// </summary>
    /// <returns>page envelope of type-catalog rows; <c>continue</c> is always null</returns>
    Task<AsyncPageResponseWriter<TypeListItem>> ListTypes();

    /// <summary>
    /// deletes an existing node
    /// </summary>
    /// <param name="nodeId">id of node to delete</param>
    Task Delete(long nodeId);

    /// <summary>
    /// links nodes
    /// </summary>
    /// <param name="sourceNodeId">id of first node</param>
    /// <param name="targetNodeId">id of second node</param>
    Task LinkNodes(long sourceNodeId, long targetNodeId);

    /// <summary>
    /// removes a link between nodes
    /// </summary>
    /// <param name="sourceNodeId">id of first node</param>
    /// <param name="targetNodeId">id of second node</param>
    Task UnlinkNodes(long sourceNodeId, long targetNodeId);

    /// <summary>
    /// resolves a node-id to the auth user-id of the user whose
    /// <see cref="Backend.Models.Users.User.HomeNodeId"/> equals <paramref name="nodeId"/>.
    ///
    /// the relation is treated as 1:1; if two user records share the same
    /// <c>HomeNodeId</c> one is returned arbitrarily.
    /// </summary>
    /// <param name="nodeId">id of the node to resolve</param>
    /// <returns>auth user-id of the user bound to this node</returns>
    /// <exception cref="Pooshit.AspNetCore.Services.Errors.Exceptions.NotFoundException{User}">
    /// thrown when no <c>divoid_user</c> row has <c>HomeNodeId == nodeId</c>.
    /// maps to HTTP 404 via the existing error-handler middleware.
    /// </exception>
    Task<long> GetUserIdForNode(long nodeId);
}