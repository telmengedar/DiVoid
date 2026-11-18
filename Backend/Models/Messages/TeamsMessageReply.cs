using Pooshit.Ocelot.Entities.Attributes;

namespace Backend.Models.Messages;

/// <summary>A private independently claimed logical send within a retained Teams origin.</summary>
public class TeamsMessageReply {
    /// <summary>The original inbox identifier which supplies authorization and destination.</summary>
    [Unique("reply"), Index("origin")]
    public long MessageId { get; set; }

    /// <summary>The caller's canonical nonzero UUID for this logical message.</summary>
    [Unique("reply"), Size(36)]
    public string ReplyId { get; set; }

    /// <summary>The immutable text digest for this logical message.</summary>
    [Size(64)]
    public string BodyHash { get; set; }

    /// <summary>The durable claim and external outcome.</summary>
    [DefaultValue(0)]
    public TeamsReplyState State { get; set; }

    /// <summary>The active attempt's UTC Unix seconds.</summary>
    public long? AttemptStartedAt { get; set; }

    /// <summary>The Connector-confirmed outbound activity identifier.</summary>
    public string OutboundActivityId { get; set; }

    /// <summary>The non-sensitive failure classification.</summary>
    public string ErrorCategory { get; set; }
}
