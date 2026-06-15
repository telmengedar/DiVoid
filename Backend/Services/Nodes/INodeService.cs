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
    /// <param name="callerId">DiVoid user-id of the authenticated caller; becomes the node's OwnerId</param>
    /// <param name="accessibleOrgs">caller's accessible organization-ids; null = admin-equivalent (no filter)</param>
    /// <param name="isAdmin">true when the caller holds the admin permission</param>
    /// <returns>created node</returns>
    Task<NodeDetails> CreateNode(NodeDetails node, long callerId, long[] accessibleOrgs = null, bool isAdmin = true);

    /// <summary>
    /// get a node by id
    /// </summary>
    /// <param name="nodeId">id of node to get</param>
    /// <param name="callerId">DiVoid user-id of the caller; used for per-node access check</param>
    /// <param name="isAdmin">true when the caller holds the admin permission</param>
    /// <param name="accessibleOrgs">caller's accessible organization-ids; null = admin-equivalent (no filter)</param>
    /// <returns><see cref="NodeDetails"/></returns>
    Task<NodeDetails> GetNodeById(long nodeId, long callerId, bool isAdmin, long[] accessibleOrgs = null);

    /// <summary>
    /// lists existing nodes
    /// </summary>
    /// <param name="filter">filter to apply</param>
    /// <param name="callerId">DiVoid user-id of the caller; used for visibility predicate</param>
    /// <param name="isAdmin">true when the caller holds the admin permission</param>
    /// <param name="accessibleOrgs">caller's accessible organization-ids; null = admin-equivalent (no filter)</param>
    /// <param name="ct">cancellation token bound to the HTTP request lifetime</param>
    /// <returns>page of nodes matching filter</returns>
    Task<AsyncPageResponseWriter<NodeDetails>> ListPaged(NodeFilter filter, long callerId, bool isAdmin, long[] accessibleOrgs = null, CancellationToken ct = default);

    /// <summary>
    /// lists nodes reachable via a graph path expression
    /// </summary>
    /// <param name="filter">path filter with the raw path string and paging parameters</param>
    /// <param name="callerId">DiVoid user-id of the caller; used for visibility predicate on the terminal hop</param>
    /// <param name="isAdmin">true when the caller holds the admin permission</param>
    /// <param name="ct">cancellation token from the HTTP request</param>
    /// <returns>page of terminal-hop nodes</returns>
    Task<AsyncPageResponseWriter<NodeDetails>> ListPagedByPath(NodePathFilter filter, long callerId, bool isAdmin, CancellationToken ct, long[] accessibleOrgs = null);

    /// <summary>
    /// get data of a node
    /// </summary>
    /// <param name="nodeId">id of node</param>
    /// <param name="callerId">DiVoid user-id of the caller; used for per-node access check</param>
    /// <param name="isAdmin">true when the caller holds the admin permission</param>
    /// <returns>content type and data of node</returns>
    Task<(string, Stream)> GetNodeData(long nodeId, long callerId, bool isAdmin, long[] accessibleOrgs = null);

    /// <summary>
    /// patches data of a node.
    /// on Postgres, a patch that touches <c>/name</c> also regenerates the node's embedding
    /// inside the same transaction (new name + current content composition).
    /// </summary>
    /// <param name="nodeId">id of node to patch</param>
    /// <param name="patches">patches to apply</param>
    /// <param name="callerId">DiVoid user-id of the caller; used for per-node and per-property access checks</param>
    /// <param name="isAdmin">true when the caller holds the admin permission</param>
    /// <param name="ct">cancellation token</param>
    /// <returns>patched node</returns>
    Task<NodeDetails> Patch(long nodeId, PatchOperation[] patches, long callerId, bool isAdmin, CancellationToken ct, long[] accessibleOrgs = null);

    /// <summary>
    /// uploads data for a node.
    /// on Postgres: also (re)generates a vector embedding from the node's name plus the
    /// new content (or name alone if content is non-text/empty; null only if both are empty).
    /// on SQLite: content is written; embedding is not touched.
    /// </summary>
    /// <param name="nodeId">id of node for which to upload data</param>
    /// <param name="contentType">content type of data</param>
    /// <param name="data">data to upload</param>
    /// <param name="callerId">DiVoid user-id of the caller; used for per-node access check</param>
    /// <param name="isAdmin">true when the caller holds the admin permission</param>
    /// <param name="ct">cancellation token</param>
    Task UploadContent(long nodeId, string contentType, Stream data, long callerId, bool isAdmin, long[] accessibleOrgs = null, CancellationToken ct = default);

    /// <summary>
    /// lists adjacency rows whose endpoints are visible to the caller; supplied ids are filtered through the org+access gate first.
    /// </summary>
    /// <param name="ids">node ids whose incident links are requested</param>
    /// <param name="filter">paging/sort filter</param>
    /// <param name="callerId">DiVoid user-id of the caller; used for per-node access check on supplied ids</param>
    /// <param name="isAdmin">true when the caller holds the admin permission</param>
    /// <param name="accessibleOrgs">caller's accessible organization-ids; null = admin-equivalent (no filter)</param>
    /// <param name="ct">cancellation token</param>
    /// <returns>page of link adjacency pairs</returns>
    Task<AsyncPageResponseWriter<NodeLink>> ListLinks(long[] ids, ListFilter filter, long callerId, bool isAdmin, long[] accessibleOrgs, CancellationToken ct);

    /// <summary>
    /// lists all node types that are currently in use (i.e. have at least one referencing node),
    /// ordered by count descending then type name ascending.
    /// returns an empty result for a graph with no nodes.
    /// </summary>
    /// <param name="ct">cancellation token bound to the HTTP request lifetime</param>
    /// <returns>page envelope of type-catalog rows; <c>continue</c> is always null</returns>
    Task<AsyncPageResponseWriter<TypeListItem>> ListTypes(CancellationToken ct = default);

    /// <summary>
    /// deletes an existing node
    /// </summary>
    /// <param name="nodeId">id of node to delete</param>
    /// <param name="callerId">DiVoid user-id of the caller; used for per-node access check</param>
    /// <param name="isAdmin">true when the caller holds the admin permission</param>
    Task Delete(long nodeId, long callerId, bool isAdmin, long[] accessibleOrgs = null);

    /// <summary>
    /// links nodes
    /// </summary>
    /// <param name="sourceNodeId">id of first node</param>
    /// <param name="targetNodeId">id of second node</param>
    /// <param name="callerId">DiVoid user-id of the caller; write required on source node</param>
    /// <param name="isAdmin">true when the caller holds the admin permission</param>
    Task LinkNodes(long sourceNodeId, long targetNodeId, long callerId, bool isAdmin, long[] accessibleOrgs = null);

    /// <summary>
    /// removes a link between nodes
    /// </summary>
    /// <param name="sourceNodeId">id of first node</param>
    /// <param name="targetNodeId">id of second node</param>
    /// <param name="callerId">DiVoid user-id of the caller; write required on source node</param>
    /// <param name="isAdmin">true when the caller holds the admin permission</param>
    Task UnlinkNodes(long sourceNodeId, long targetNodeId, long callerId, bool isAdmin, long[] accessibleOrgs = null);

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