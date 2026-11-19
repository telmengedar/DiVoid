namespace Backend.Models.Messages;

/// <summary>The safe receipt of one independently idempotent logical reply.</summary>
public class MessageReplyDetails : MessageReplyReceipt {
    /// <summary>The caller's logical-message UUID.</summary>
    public Guid ReplyId { get; set; }
}
