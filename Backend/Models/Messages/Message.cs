using Pooshit.Ocelot.Entities.Attributes;

namespace Backend.Models.Messages;

/// <summary>
/// a message sent from one user to another
/// </summary>
public class Message {

    /// <summary>
    /// id of message
    /// </summary>
    [PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    /// <summary>
    /// id of the user who sent this message
    /// </summary>
    [Index("author")]
    public long AuthorId { get; set; }

    /// <summary>
    /// id of the user who is the intended recipient of this message
    /// </summary>
    [Index("recipient")]
    public long RecipientId { get; set; }

    /// <summary>
    /// subject of the message; trimmed of leading and trailing whitespace by the service.
    /// non-empty and at most 256 characters enforced server-side.
    /// </summary>
    [Size(256)]
    public string Subject { get; set; }

    /// <summary>
    /// body of the message in Markdown format.
    /// size is capped at the pipeline level by the existing Kestrel MaxRequestBodySize limit,
    /// not by a per-property cap on this field.
    /// </summary>
    public string Body { get; set; }

    /// <summary>
    /// UTC timestamp when the message was created.
    /// set server-side via DateTime.UtcNow at insert time; clients cannot supply this.
    /// part of the composite (RecipientId, CreatedAt) index that covers the hot inbox path.
    /// </summary>
    [Index("recipient")]
    public DateTime CreatedAt { get; set; }
}
