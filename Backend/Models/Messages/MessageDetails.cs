namespace Backend.Models.Messages;

/// <summary>
/// wire shape returned by the messaging API for a single message.
/// carries author and recipient as ids only — name resolution is the client's responsibility.
/// no write-only fields; all fields round-trip unchanged on GET.
/// </summary>
public class MessageDetails {

    /// <summary>
    /// id of the message
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// id of the user who sent this message
    /// </summary>
    public long AuthorId { get; set; }

    /// <summary>
    /// id of the user who is the intended recipient of this message
    /// </summary>
    public long RecipientId { get; set; }

    /// <summary>
    /// subject of the message; at most 256 characters
    /// </summary>
    public string Subject { get; set; }

    /// <summary>
    /// body of the message in Markdown format.
    /// excluded from the default list projection — callers who want the body in list
    /// responses must pass <c>?fields=id,authorId,recipientId,subject,body,createdat</c>.
    /// </summary>
    public string Body { get; set; }

    /// <summary>
    /// UTC timestamp when the message was created
    /// </summary>
    public DateTime CreatedAt { get; set; }
}
