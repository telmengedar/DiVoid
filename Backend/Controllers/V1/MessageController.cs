using Backend.Auth;
using Backend.Models.Messages;
using Backend.Services.Messages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pooshit.AspNetCore.Services.Formatters.DataStream;

namespace Backend.Controllers.V1;

/// <summary>
/// manages messages between users
/// </summary>
[Route("api/messages")]
[ApiController]
public class MessageController(ILogger<MessageController> logger, IMessageService messageService) : ControllerBase {
    readonly ILogger<MessageController> logger = logger;
    readonly IMessageService messageService = messageService;


    /// <summary>
    /// sends a message to another user.
    /// the authenticated caller is always recorded as the author regardless of any authorId
    /// supplied in the request body — clients cannot impersonate another sender.
    /// </summary>
    /// <param name="details">message to send; recipientId, subject, and body are required</param>
    /// <returns>persisted message details including server-assigned id and createdAt</returns>
    [HttpPost]
    [Authorize(Policy = "write")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public Task<MessageDetails> Create([FromBody] MessageDetails details) {
        long callerId = User.GetDivoidUserId();
        logger.LogInformation("event=message.create author={CallerId} recipient={RecipientId}", callerId, details?.RecipientId);
        return messageService.Create(callerId, details);
    }


    /// <summary>
    /// lists messages visible to the authenticated caller.
    /// non-admin callers see only messages where they are the author or recipient.
    /// </summary>
    /// <param name="filter">paging, sort, and id filter criteria</param>
    /// <returns>streamed page of message details</returns>
    [HttpGet]
    [Authorize(Policy = "read")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    public Task<AsyncPageResponseWriter<MessageDetails>> List([FromQuery] MessageFilter filter) {
        long callerId = User.GetDivoidUserId();
        bool isAdmin = User.HasClaim("permission", "admin");
        return messageService.ListPaged(callerId, isAdmin, filter);
    }


    /// <summary>
    /// gets a single message by id.
    /// returns 404 if the message does not exist or the caller is neither the author, the recipient, nor an admin
    /// — existence is not revealed to unauthorised callers.
    /// </summary>
    /// <param name="id">id of the message to retrieve</param>
    /// <returns>details of the requested message</returns>
    [HttpGet("{id:long}")]
    [Authorize(Policy = "read")]
    [ProducesResponseType(200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public Task<MessageDetails> GetById(long id) {
        long callerId = User.GetDivoidUserId();
        bool isAdmin = User.HasClaim("permission", "admin");
        return messageService.GetById(callerId, isAdmin, id);
    }


    /// <summary>
    /// deletes a message.
    /// only the recipient or an admin may delete; the sender cannot recall a sent message.
    /// returns 404 for both non-existent messages and messages the caller is not allowed to delete
    /// (existence is not revealed to unauthorized callers).
    /// </summary>
    /// <param name="id">id of the message to delete</param>
    [HttpDelete("{id:long}")]
    [Authorize(Policy = "write")]
    [ProducesResponseType(200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public Task DeleteById(long id) {
        long callerId = User.GetDivoidUserId();
        bool isAdmin = User.HasClaim("permission", "admin");
        logger.LogInformation("event=message.delete by={CallerId} id={MessageId}", callerId, id);
        return messageService.Delete(callerId, isAdmin, id);
    }
}
