using System.Threading;
using Backend.Auth;
using Backend.Models.Nodes;
using Backend.Models.Users;
using Backend.Services.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pooshit.AspNetCore.Services.Data;
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
        /// resolves caller identity from the current principal.
        /// returns (0, true) when auth is disabled or the principal carries no identity — admin-equivalent posture.
        /// </summary>
        (long callerId, bool isAdmin) ResolveCaller()
        {
            if (User.Identity?.IsAuthenticated != true)
                return (0L, true);
            long callerId = 0L;
            string idValue = User.FindFirst(ClaimsExtensions.DivoidUserIdClaimType)?.Value;
            if (idValue != null)
                long.TryParse(idValue, out callerId);
            bool isAdmin = User.HasClaim("permission", "admin");
            return (callerId, isAdmin);
        }


        /// <summary>
        /// creates a new <see cref="Node"/>.
        /// on Postgres, creates a name-only embedding for the new node if its name is non-empty.
        /// </summary>
        /// <param name="node">node to create</param>
        /// <returns>created node</returns>
        [HttpPost]
        [Authorize(Policy = "write")]
        public Task<NodeDetails> CreateNode([FromBody] NodeDetails node)
        {
            logger.LogInformation("Creating node '{name}'", node.Name);
            (long callerId, bool _) = ResolveCaller();
            return nodeService.CreateNode(node, callerId);
        }

        /// <summary>
        /// get a node by id
        /// </summary>
        /// <param name="nodeId">id of node to get</param>
        /// <returns><see cref="NodeDetails"/></returns>
        [HttpGet("{nodeId:long}")]
        [Authorize(Policy = "read")]
        public Task<NodeDetails> GetNodeById(long nodeId)
        {
            (long callerId, bool isAdmin) = ResolveCaller();
            return nodeService.GetNodeById(nodeId, callerId, isAdmin);
        }

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
            (long callerId, bool isAdmin) = ResolveCaller();
            if (!string.IsNullOrEmpty(filter?.Path))
            {
                logger.LogInformation("Path query: {Path}", filter.Path);
                return nodeService.ListPagedByPath(filter, callerId, isAdmin, ct);
            }
            return nodeService.ListPaged(filter, callerId, isAdmin, ct);
        }

        /// <summary>
        /// returns all link adjacency rows where either endpoint is in <paramref name="ids"/>.
        /// used by the workspace viewport to fetch edges incident to visible nodes in one round-trip.
        /// </summary>
        /// <param name="ids">comma-separated list of node ids whose incident links are requested</param>
        /// <param name="filter">paging filter</param>
        /// <param name="ct">cancellation token bound to the HTTP request lifetime</param>
        /// <returns>page of link adjacency pairs in the standard list envelope</returns>
        [HttpGet("links")]
        [Authorize(Policy = "read")]
        public Task<AsyncPageResponseWriter<NodeLink>> ListLinks([FromQuery] long[] ids, [FromQuery] ListFilter filter, CancellationToken ct)
        {
            logger.LogInformation("Listing links for {Count} nodes", ids?.Length ?? 0);
            return nodeService.ListLinks(ids ?? [], filter, ct);
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
            (long callerId, bool isAdmin) = ResolveCaller();
            (string contentType, Stream data) = await nodeService.GetNodeData(nodeId, callerId, isAdmin);
            return File(data, contentType);
        }

        /// <summary>
        /// resolves a node-id to the auth user-id of the user bound to it via
        /// <c>HomeNodeId</c>. only requires <c>read</c> permission so non-admin
        /// agents can look up their own or other agents' user-ids without
        /// elevated access.
        ///
        /// returns 404 both when no user has <c>HomeNodeId == nodeId</c> and when
        /// the node itself does not exist — the endpoint does not distinguish
        /// between these cases to avoid probing node existence.
        /// </summary>
        /// <param name="nodeId">id of the node to resolve to a user-id</param>
        /// <returns>user-id of the user bound to this node</returns>
        [HttpGet("{nodeId:long}/user")]
        [Authorize(Policy = "read")]
        public async Task<UserIdResponse> GetUser(long nodeId)
        {
            logger.LogInformation("event=node.user.lookup nodeId={NodeId} callerId={CallerId}", nodeId, User.GetDivoidUserId());
            return new UserIdResponse { UserId = await nodeService.GetUserIdForNode(nodeId) };
        }

        /// <summary>
        /// patches data of a node.
        /// on Postgres, a patch that touches <c>/name</c> also regenerates the node's embedding
        /// inside the same transaction (new name + current content composition).
        /// </summary>
        /// <param name="nodeId">id of node to patch</param>
        /// <param name="patches">patches to apply</param>
        /// <param name="ct">cancellation token bound to the HTTP request lifetime</param>
        /// <returns>patched node</returns>
        [HttpPatch("{nodeId:long}")]
        [Authorize(Policy = "write")]
        public Task<NodeDetails> Patch(long nodeId, [FromBody] PatchOperation[] patches, CancellationToken ct)
        {
            logger.LogInformation("Patching node '{nodeId}'", nodeId);
            (long callerId, bool isAdmin) = ResolveCaller();
            return nodeService.Patch(nodeId, patches, callerId, isAdmin, ct);
        }

        /// <summary>
        /// uploads data for a node.
        /// on Postgres: also (re)generates a vector embedding from the node's name plus the
        /// new content (or name alone if content is non-text/empty; null only if both are empty).
        /// on SQLite: content is written; embedding is not touched.
        /// </summary>
        /// <param name="nodeId">id of node for which to upload data</param>
        /// <param name="ct">cancellation token bound to the HTTP request lifetime</param>
        [HttpPost("{nodeId:long}/content")]
        [Authorize(Policy = "write")]
        public Task UploadContent(long nodeId, CancellationToken ct)
        {
            logger.LogInformation("Updating content of node '{nodeId}'", nodeId);
            (long callerId, bool isAdmin) = ResolveCaller();
            return nodeService.UploadContent(nodeId, Request.ContentType, Request.Body, callerId, isAdmin, ct);
        }

        /// <summary>
        /// applies partial edits to a node's existing text content — an ordered list of
        /// range replacements (replace / insert / delete / append) addressed by line or
        /// character, all against the content as read.  a safer alternative to a wholesale
        /// content re-upload via <c>POST /content</c>.
        /// on Postgres the embedding is regenerated in the same transaction.
        /// </summary>
        /// <param name="nodeId">id of the node whose content is edited</param>
        /// <param name="edits">ordered range replacements to apply</param>
        /// <param name="ct">cancellation token bound to the HTTP request lifetime</param>
        /// <returns>the updated node</returns>
        [HttpPatch("{nodeId:long}/content")]
        [Authorize(Policy = "write")]
        public Task<NodeDetails> PatchContent(long nodeId, [FromBody] ContentEdit[] edits, CancellationToken ct)
        {
            logger.LogInformation("Editing content of node '{nodeId}' with {count} edit(s)", nodeId, edits?.Length ?? 0);
            (long callerId, bool isAdmin) = ResolveCaller();
            return nodeService.PatchContent(nodeId, edits, callerId, isAdmin, ct);
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
            (long callerId, bool isAdmin) = ResolveCaller();
            return nodeService.Delete(nodeId, callerId, isAdmin);
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
            (long callerId, bool isAdmin) = ResolveCaller();
            return nodeService.LinkNodes(sourceNodeId, targetNodeId, callerId, isAdmin);
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
            (long callerId, bool isAdmin) = ResolveCaller();
            return nodeService.UnlinkNodes(sourceNodeId, targetNodeId, callerId, isAdmin);
        }

    }
}
