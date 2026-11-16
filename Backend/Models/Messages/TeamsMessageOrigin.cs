using Pooshit.Ocelot.Entities.Attributes;

namespace Backend.Models.Messages;

/// <summary>The private authenticated return route and receipt for a Teams inbox message.</summary>
public class TeamsMessageOrigin {
    /// <summary>The original inbox message identifier.</summary>
    [PrimaryKey, DefaultValue(0)]
    public long MessageId { get; set; }

    /// <summary>The canonical inbound identity digest.</summary>
    [Unique("dedup"), Size(64)]
    public string DedupKey { get; set; }

    /// <summary>The authenticated configured application identifier.</summary>
    public string BotAppId { get; set; }

    /// <summary>The authenticated Teams tenant identifier.</summary>
    public string TenantId { get; set; }

    /// <summary>The original authorized recipient.</summary>
    [DefaultValue(0)]
    public long RecipientId { get; set; }

    /// <summary>The authenticated Connector service URL.</summary>
    public string ServiceUrl { get; set; }

    /// <summary>The original conversation identifier.</summary>
    public string ConversationId { get; set; }

    /// <summary>The inbound activity identifier.</summary>
    public string ActivityId { get; set; }

    /// <summary>The native bot account identifier.</summary>
    public string BotId { get; set; }

    /// <summary>The native external sender identifier.</summary>
    public string SenderId { get; set; }

    /// <summary>The inbox consumption time.</summary>
    public DateTime? ConsumedAt { get; set; }

    /// <summary>The retained route expiration.</summary>
    [Index("expiry")]
    public DateTime? ExpiresAt { get; set; }

    /// <summary>The explicit reply receipt state.</summary>
    [DefaultValue(0)]
    public TeamsReplyState ReplyState { get; set; }

    /// <summary>The claimed final reply body digest.</summary>
    [Size(64)]
    public string ReplyBodyHash { get; set; }

    /// <summary>The external attempt start time.</summary>
    public DateTime? ReplyAttemptStartedAt { get; set; }

    /// <summary>The confirmed outbound activity identifier.</summary>
    public string OutboundActivityId { get; set; }

    /// <summary>The non-sensitive failure classification.</summary>
    public string ErrorCategory { get; set; }
}
