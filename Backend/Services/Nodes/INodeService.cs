using System.Threading;
using Backend.Models.Nodes;
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
    /// <returns>page of nodes matching filter</returns>
    Task<AsyncPageResponseWriter<NodeDetails>> ListPaged(NodeFilter filter = null);

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
    /// patches data of a node
    /// </summary>
    /// <param name="nodeId">id of node to patch</param>
    /// <param name="patches">patches to apply</param>
    /// <returns>patched nodes</returns>
    Task<NodeDetails> Patch(long nodeId, params PatchOperation[] patches);

    /// <summary>
    /// uploads data for a node
    /// </summary>
    /// <param name="nodeId">id of node for which to upload data</param>
    /// <param name="contentType">content type of data</param>
    /// <param name="data">data to upload</param>
    Task UploadContent(long nodeId, string contentType, Stream data);

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
}