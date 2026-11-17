namespace Backend.Models.Messages;

/// <summary>The public receipt for an explicit Teams reply.</summary>
public class MessageReplyReceipt {
    /// <summary>The original inbox message identifier.</summary>
    public long MessageId { get; set; }

    /// <summary>The external reply transport.</summary>
    public string Transport { get; set; }

    /// <summary>The confirmed or uncertain attempt state.</summary>
    public string State { get; set; }

    /// <summary>The outbound activity identifier confirmed by the Connector.</summary>
    public string OutboundActivityId { get; set; }
}
