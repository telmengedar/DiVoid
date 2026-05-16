using System.Threading.Tasks;
using Backend.Models.Messages;
using Pooshit.AspNetCore.Services.Formatters.DataStream;

namespace Backend.Services.Messages;

/// <summary>
/// service used to manage messages between users
/// </summary>
public interface IMessageService {

    /// <summary>
    /// creates a new message.
    /// the <paramref name="callerId"/> is always written as the author regardless of any
    /// <c>AuthorId</c> value in <paramref name="details"/> — clients cannot impersonate.
    /// </summary>
    /// <param name="callerId">id of the authenticated user sending the message</param>
    /// <param name="details">message data; only RecipientId, Subject, and Body are used</param>
    /// <returns>details of the persisted message including server-assigned Id and CreatedAt</returns>
    Task<MessageDetails> Create(long callerId, MessageDetails details);

    /// <summary>
    /// gets a single message by id.
    /// applies row-level authorization: the caller must be admin, the author, or the recipient.
    /// </summary>
    /// <param name="callerId">id of the authenticated caller</param>
    /// <param name="isAdmin">whether the caller holds the admin permission</param>
    /// <param name="id">id of the message to retrieve</param>
    /// <returns>details of the message</returns>
    Task<MessageDetails> GetById(long callerId, bool isAdmin, long id);

    /// <summary>
    /// lists messages visible to the caller.
    /// non-admin callers see only messages where they are the author or the recipient.
    /// caller-supplied filters in <paramref name="filter"/> are ANDed on top of that scope.
    /// default sort is createdat DESC when filter.Sort is empty.
    /// </summary>
    /// <param name="callerId">id of the authenticated caller</param>
    /// <param name="isAdmin">whether the caller holds the admin permission</param>
    /// <param name="filter">paging, sort, and id filters to apply</param>
    /// <returns>streamed page of message details</returns>
    Task<AsyncPageResponseWriter<MessageDetails>> ListPaged(long callerId, bool isAdmin, MessageFilter filter = null);

    /// <summary>
    /// deletes a message.
    /// only the recipient or an admin may delete; sender cannot recall.
    /// collapses "not found" and "caller not allowed to delete" into a single NotFoundException
    /// per the optimistic-delete pattern used throughout this codebase.
    /// </summary>
    /// <param name="callerId">id of the authenticated caller</param>
    /// <param name="isAdmin">whether the caller holds the admin permission</param>
    /// <param name="id">id of the message to delete</param>
    Task Delete(long callerId, bool isAdmin, long id);
}
