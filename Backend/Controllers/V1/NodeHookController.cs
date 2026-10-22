using Backend.Auth;
using Backend.Models.Nodes;
using Backend.Services.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers.V1;

/// <summary>manages node subscriptions</summary>
[Route("api/nodehooks")]
[ApiController]
public class NodeHookController(INodeService nodeService) : ControllerBase {
    /// <summary>registers a subscription</summary>
    /// <param name="hook">subscription tuple</param>
    /// <returns>registered subscription</returns>
    [HttpPost, Authorize(Policy = "write")]
    public Task<NodeHook> Register([FromBody] NodeHook hook) =>
        nodeService.RegisterHook(hook, User.GetDivoidUserId(), User.HasClaim("permission", "admin"));


    /// <summary>lists subscriptions</summary>
    /// <param name="nodeId">optional node filter</param>
    /// <param name="userId">optional recipient filter</param>
    /// <returns>subscriptions</returns>
    [HttpGet, Authorize(Policy = "read")]
    public Task<NodeHook[]> List([FromQuery] long? nodeId, [FromQuery] long? userId) =>
        nodeService.ListHooks(nodeId, userId, User.GetDivoidUserId(), User.HasClaim("permission", "admin"));


    /// <summary>removes a subscription</summary>
    /// <param name="hook">subscription tuple</param>
    [HttpDelete, Authorize(Policy = "write")]
    public Task Delete([FromBody] NodeHook hook) =>
        nodeService.DeleteHook(hook, User.GetDivoidUserId(), User.HasClaim("permission", "admin"));
}
