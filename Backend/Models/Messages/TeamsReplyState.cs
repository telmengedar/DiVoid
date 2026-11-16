namespace Backend.Models.Messages;

/// <summary>The persisted state of one explicit external reply.</summary>
public enum TeamsReplyState {
    /// <summary>No reply has been attempted.</summary>
    Pending,
    /// <summary>An external attempt is in progress or requires reconciliation.</summary>
    Sending,
    /// <summary>The Connector confirmed acceptance.</summary>
    Sent,
    /// <summary>A definitive rejection permits an explicit retry.</summary>
    Failed,
    /// <summary>The external outcome requires reconciliation.</summary>
    Unknown
}
