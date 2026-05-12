using System;

namespace Backend.Services.Auth;

/// <summary>
/// thrown when <c>Keycloak:Audience</c> is empty and <c>Auth:Enabled</c> is true,
/// causing the service to refuse to start fail-closed
/// </summary>
public class MissingAudienceException : InvalidOperationException {

    /// <summary>
    /// creates a new <see cref="MissingAudienceException"/>
    /// </summary>
    /// <param name="message">human-readable description</param>
    public MissingAudienceException(string message) : base(message) {
    }
}
