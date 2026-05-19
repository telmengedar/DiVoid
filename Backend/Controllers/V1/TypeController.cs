using Backend.Auth;
using Backend.Models.Nodes;
using Backend.Services.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pooshit.AspNetCore.Services.Formatters.DataStream;

namespace Backend.Controllers.V1;

/// <summary>
/// exposes the live node-type vocabulary (types in use)
/// </summary>
[Route("api/types")]
[ApiController]
public class TypeController(ILogger<TypeController> logger, INodeService nodeService) : ControllerBase {
    readonly ILogger<TypeController> logger = logger;
    readonly INodeService nodeService = nodeService;


    /// <summary>
    /// lists all node types currently in use in the graph, ordered by count descending then type name ascending.
    /// orphaned <c>NodeType</c> rows (no referencing nodes) are excluded by construction.
    /// returns an empty result for a graph with no nodes.
    /// </summary>
    /// <returns>catalog of in-use types with per-type node counts</returns>
    [HttpGet]
    [Authorize(Policy = "read")]
    [ProducesResponseType(200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public Task<AsyncPageResponseWriter<TypeListItem>> ListTypes() {
        long callerId = User.GetDivoidUserId();
        logger.LogInformation("event=type.list callerId={CallerId}", callerId);
        return nodeService.ListTypes();
    }
}
