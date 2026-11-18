using Backend.Auth;
using Backend.Models.Messages;
using Backend.Services.Teams;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pooshit.AspNetCore.Services.Errors;

namespace Backend.Controllers.V1;

/// <summary>Independently idempotent replies to the authenticated original conversation.</summary>
[ApiController, Route("api/messages/{messageId:long}/replies")]
public class MessageRepliesController : ControllerBase {
    readonly TeamsMessageService transport;

    /// <summary>Creates the logical-reply resource controller.</summary>
    /// <param name="transport">The retained-origin transport.</param>
    public MessageRepliesController(TeamsMessageService transport) {
        this.transport = transport;
    }

    /// <summary>Reads one original-recipient-authorized logical reply receipt.</summary>
    /// <param name="messageId">The original inbox identifier.</param>
    /// <param name="replyId">The saved logical-message UUID.</param>
    /// <returns>The safe receipt, without private origin fields.</returns>
    [HttpGet("{replyId}"), Authorize(Policy = "read")]
    [ProducesResponseType(200, Type = typeof(MessageReplyDetails))]
    [ProducesResponseType(400, Type = typeof(ErrorResponse))]
    [ProducesResponseType(401, Type = typeof(ErrorResponse))]
    [ProducesResponseType(403, Type = typeof(ErrorResponse))]
    [ProducesResponseType(404, Type = typeof(ErrorResponse))]
    [ProducesResponseType(500, Type = typeof(ErrorResponse))]
    public Task<MessageReplyDetails> Get(long messageId, string replyId) =>
        transport.GetReplyAsync(User.GetDivoidUserId(), messageId, ParseId(replyId));

    /// <summary>Attempts one explicit logical response to the saved original conversation.</summary>
    /// <param name="messageId">The original inbox identifier.</param>
    /// <param name="reply">The saved UUID and exact text.</param>
    /// <param name="cancellationToken">The requesting caller's cancellation.</param>
    /// <returns>The confirmed or previously accepted receipt.</returns>
    [HttpPost, Authorize(Policy = "write")]
    [ProducesResponseType(200, Type = typeof(MessageReplyDetails))]
    [ProducesResponseType(400, Type = typeof(ErrorResponse))]
    [ProducesResponseType(401, Type = typeof(ErrorResponse))]
    [ProducesResponseType(403, Type = typeof(ErrorResponse))]
    [ProducesResponseType(404, Type = typeof(ErrorResponse))]
    [ProducesResponseType(409, Type = typeof(ErrorResponse))]
    [ProducesResponseType(500, Type = typeof(ErrorResponse))]
    [ProducesResponseType(502, Type = typeof(ErrorResponse))]
    public Task<MessageReplyDetails> Create(long messageId, [FromBody] MessageReplyRequest reply, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(reply);
        return transport.CreateReplyAsync(User.GetDivoidUserId(), messageId, ParseId(reply.ReplyId), reply.Body, cancellationToken);
    }

    static Guid ParseId(string value) => Guid.TryParse(value, out Guid id) && id != Guid.Empty
        ? id : throw new ArgumentException("replyId must be a nonzero UUID.", nameof(value));
}
