namespace Backend.Models.Messages;

/// <summary>The private-origin Connector probe result without conversation or member data.</summary>
public class MessageReplyDiagnosis {
    /// <summary>Whether the native Connector read completed successfully.</summary>
    public bool Completed { get; set; }

    /// <summary>The typed Connector HTTP failure status when available.</summary>
    public int? HttpStatus { get; set; }

    /// <summary>The allowlisted provider error code or a fixed fallback.</summary>
    public string ProviderCode { get; set; }

    /// <summary>The fixed diagnostic outcome category.</summary>
    public string Reason { get; set; }
}
