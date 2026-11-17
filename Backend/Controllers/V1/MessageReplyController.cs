using Backend.Auth;
using Backend.Models.Messages;
using Backend.Services.Teams;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers.V1;

/// <summary>Receipts and explicit external replies for the original inbox recipient.</summary>
[ApiController, Route("api/messages/{messageId:long}/reply")]
public class MessageReplyController : ControllerBase {
    readonly TeamsMessageService transport;

    /// <summary>Creates the explicit reply controller.</summary>
    /// <param name="transport">The concrete Teams reply transport.</param>
    public MessageReplyController(TeamsMessageService transport) {
        this.transport = transport;
    }

    /// <summary>Returns the recipient-authorized external reply receipt.</summary>
    /// <param name="messageId">The original inbox message identifier.</param>
    /// <returns>The public reply receipt.</returns>
    [HttpGet, Authorize(Policy = "read")]
    public Task<MessageReplyReceipt> Get(long messageId) => transport.GetAsync(User.GetDivoidUserId(), messageId);

    /// <summary>Returns one explicit final response to the original conversation.</summary>
    /// <param name="messageId">The original inbox message identifier.</param>
    /// <param name="reply">The explicitly requested response text.</param>
    /// <param name="cancellationToken">The caller request cancellation.</param>
    /// <returns>The Connector-confirmed public receipt.</returns>
    [HttpPost, Authorize(Policy = "write")]
    public Task<MessageReplyReceipt> Reply(long messageId, [FromBody] MessageReplyBody reply, CancellationToken cancellationToken) =>
        transport.ReplyAsync(User.GetDivoidUserId(), messageId, reply.Body, cancellationToken);
}
