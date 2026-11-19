namespace Backend.Models.Messages;

/// <summary>
/// wire shape returned by the messaging API for a single message.
/// carries internal author and recipient ids plus optional server-observed external display metadata.
/// no write-only fields; all fields round-trip unchanged on GET.
/// </summary>
public class MessageDetails {

    /// <summary>
    /// id of the message
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// sender user id; 0 identifies the system author
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

    /// <summary>The optional server-observed external display label, not verified identity or authority.</summary>
    public string ExternalAuthorDisplayName { get; set; }

    /// <summary>The internally assigned external reply transport.</summary>
    public string ReplyTransport { get; set; }
}
