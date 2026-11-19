namespace Backend.Models.Messages;

/// <summary>One explicit independently idempotent response to a retained origin.</summary>
public class MessageReplyRequest : MessageReplyBody {
    /// <summary>The nonzero caller-generated UUID saved before the first attempt.</summary>
    public string ReplyId { get; set; }
}
