using System;

namespace Backend.Services.Auth;

/// <summary>
/// thrown when the DIVOID_KEY_PEPPER environment variable is absent or too short
/// and Auth:Enabled is true, causing the service to refuse to start fail-closed
/// </summary>
public class MissingPepperException : InvalidOperationException {

    /// <summary>
    /// creates a new <see cref="MissingPepperException"/>
    /// </summary>
    /// <param name="message">human-readable description</param>
    public MissingPepperException(string message) : base(message) {
    }
}
