using System.Threading;
using Backend.Models.Nodes;
using Backend.Services.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.AspNetCore.Services.Patches;

namespace Backend.Controllers.V1
{

    /// <summary>
    /// controller used to manage nodes
    /// </summary>
    /// <remarks>
    /// creates a new <see cref="NodeController"/>
    /// </remarks>
    /// <param name="logger">access to logging</param>
    /// <param name="nodeService">service used to process requests</param>
    [Route("api/nodes")]
    [ApiController]
    public class NodeController(ILogger<NodeController> logger, INodeService nodeService) : ControllerBase
    {
        readonly ILogger<NodeController> logger = logger;
        readonly INodeService nodeService = nodeService;


        /// <summary>
        /// creates a new <see cref="Node"/>
        /// </summary>
        /// <param name="node">node to create</param>
        /// <returns>created node</returns>
        [HttpPost]
        [Authorize(Policy = "write")]
        public Task<NodeDetails> CreateNode([FromBody] NodeDetails node)
        {
            logger.LogInformation("Creating node '{name}'", node.Name);
            return nodeService.CreateNode(node);
        }

        /// <summary>
        /// get a node by id
        /// </summary>
        /// <param name="nodeId">id of node to get</param>
        /// <returns><see cref="NodeDetails"/></returns>
        [HttpGet("{nodeId:long}")]
        [Authorize(Policy = "read")]
        public Task<NodeDetails> GetNodeById(long nodeId) => nodeService.GetNodeById(nodeId);

        /// <summary>
        /// lists existing nodes, or — when <c>path</c> is supplied — resolves a graph path
        /// expression and returns the terminal-hop node set.
        ///
        /// Standard list mode: all filter fields apply directly.
        /// Path mode (<c>?path=...</c>): the bracketed path expression is parsed and
        /// resolved as a single server-side joined query; paging, sort, and fields apply
        /// to the terminal hop only.
        /// </summary>
        /// <param name="filter">
        /// unified filter; <c>path</c> activates graph-path mode, e.g.
        /// <c>[type:organization,name:Pooshit]/[type:project,name:DiVoid]/[type:task,status:open]</c>
        /// </param>
        /// <param name="ct">cancellation token bound to the HTTP request lifetime</param>
        /// <returns>page of nodes in the standard list envelope</returns>
        [HttpGet]
        [Authorize(Policy = "read")]
        public Task<AsyncPageResponseWriter<NodeDetails>> ListPaged([FromQuery] NodePathFilter filter, CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(filter?.Path))
            {
                logger.LogInformation("Path query: {Path}", filter.Path);
                return nodeService.ListPagedByPath(filter, ct);
            }
            return nodeService.ListPaged(filter);
        }

        /// <summary>
        /// get data of a node
        /// </summary>
        /// <param name="nodeId">id of node</param>
        /// <returns>content type and data of node</returns>
        [HttpGet("{nodeId:long}/content")]
        [Authorize(Policy = "read")]
        public async Task<IActionResult> GetNodeData(long nodeId)
        {
            (string contentType, Stream data) = await nodeService.GetNodeData(nodeId);
            return File(data, contentType);
        }

        /// <summary>
        /// patches data of a node
        /// </summary>
        /// <param name="nodeId">id of node to patch</param>
        /// <param name="patches">patches to apply</param>
        /// <returns>patched nodes</returns>
        [HttpPatch("{nodeId:long}")]
        [Authorize(Policy = "write")]
        public Task<NodeDetails> Patch(long nodeId, [FromBody] PatchOperation[] patches)
        {
            logger.LogInformation("Patching node '{nodeId}'", nodeId);
            return nodeService.Patch(nodeId, patches);
        }

        /// <summary>
        /// uploads data for a node
        /// </summary>
        /// <param name="nodeId">id of node for which to upload data</param>
        /// <param name="contentType">content type of data</param>
        /// <param name="data">data to upload</param>
        [HttpPost("{nodeId:long}/content")]
        [Authorize(Policy = "write")]
        public Task UploadContent(long nodeId)
        {
            logger.LogInformation("Updating content of node '{nodeId}'", nodeId);
            return nodeService.UploadContent(nodeId, Request.ContentType, Request.Body);
        }

        /// <summary>
        /// deletes an existing node
        /// </summary>
        /// <param name="nodeId">id of node to delete</param>
        [HttpDelete("{nodeId:long}")]
        [Authorize(Policy = "write")]
        public Task Delete(long nodeId)
        {
            logger.LogInformation("Deleting node '{nodeId}'", nodeId);
            return nodeService.Delete(nodeId);
        }

        /// <summary>
        /// links nodes
        /// </summary>
        /// <param name="sourceNodeId">id of first node</param>
        /// <param name="targetNodeId">id of second node</param>
        [HttpPost("{sourceNodeId:long}/links")]
        [Authorize(Policy = "write")]
        public Task LinkNodes(long sourceNodeId, [FromBody] long targetNodeId)
        {
            logger.LogInformation("Linking node '{targetNodeId}' to '{sourceNodeId}'", targetNodeId, sourceNodeId);
            return nodeService.LinkNodes(sourceNodeId, targetNodeId);
        }

        /// <summary>
        /// removes a link between nodes
        /// </summary>
        /// <param name="sourceNodeId">id of first node</param>
        /// <param name="targetNodeId">id of second node</param>
        [HttpDelete("{sourceNodeId:long}/links/{targetNodeId}")]
        [Authorize(Policy = "write")]
        public Task UnlinkNodes(long sourceNodeId, long targetNodeId)
        {
            logger.LogInformation("Unlinking '{targetNodeId}' from '{sourceNodeId}'", targetNodeId, sourceNodeId);
            return nodeService.UnlinkNodes(sourceNodeId, targetNodeId);
        }

    }
}
