namespace Backend.Models.Messages;

/// <summary>The private-origin Connector result with decoded-token diagnostics, without signature validation.</summary>
public class MessageReplyDiagnosis {
    /// <summary>Whether the native Connector read completed successfully.</summary>
    public bool Completed { get; set; }

    /// <summary>The typed Connector HTTP failure status when available.</summary>
    public int? HttpStatus { get; set; }

    /// <summary>The allowlisted provider error code or a fixed fallback.</summary>
    public string ProviderCode { get; set; }

    /// <summary>The fixed diagnostic outcome category.</summary>
    public string Reason { get; set; }

    /// <summary>Whether the observed native request carries exactly one nonblank Bearer token.</summary>
    public bool? HeaderSingleBearer { get; set; }

    /// <summary>Whether the observed token is readable as a JWT; false does not exclude opaque tokens.</summary>
    public bool? JwtReadable { get; set; }

    /// <summary>Whether the decoded audience equals the qualified Connector resource, when available.</summary>
    public bool? AudienceMatchesConnectorResource { get; set; }

    /// <summary>Whether every available decoded application identity matches the configured bot.</summary>
    public bool? AuthorizedPartyMatchesConfiguredBot { get; set; }

    /// <summary>Whether the available decoded issuer matches the qualified Bot Framework tenant.</summary>
    public bool? IssuerMatchesKnownBotFrameworkIssuer { get; set; }

    /// <summary>Whether the available decoded validity interval includes the observation time.</summary>
    public bool? ValidityWindowMatchesNow { get; set; }

    /// <summary>Whether the native request targets exactly the retained conversation members route.</summary>
    public bool? OutboundRouteMatchesRetainedOrigin { get; set; }

    /// <summary>Whether the native request context names the configured bot.</summary>
    public bool? RequestContextBotMatchesConfiguredBot { get; set; }

    /// <summary>The fixed response authentication challenge class, when a response was observed.</summary>
    public string AuthenticationChallenge { get; set; }

    /// <summary>The allowlisted Bearer challenge error or a fixed inconclusive class.</summary>
    public string AuthenticationChallengeError { get; set; }

    /// <summary>The fixed explicit challenge authority class, without authority or tenant values.</summary>
    public string AuthenticationChallengeAuthority { get; set; }

    /// <summary>Whether available explicit challenge resources match the qualified Connector resource.</summary>
    public bool? AuthenticationChallengeResourceMatchesConnector { get; set; }
}
