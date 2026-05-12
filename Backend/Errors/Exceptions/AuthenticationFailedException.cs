namespace Backend.Errors.Exceptions;

/// <summary>
/// thrown by auth handlers when a request cannot be authenticated (HTTP 401).
/// the <see cref="System.Exception.Message"/> becomes the <c>text</c> field in the
/// canonical <c>{ code, text }</c> error response produced by
/// <see cref="AuthenticationFailedExceptionHandler"/>.
/// </summary>
public class AuthenticationFailedException : Exception {
    /// <summary>
    /// creates a new <see cref="AuthenticationFailedException"/> with a human-readable detail
    /// </summary>
    /// <param name="detail">human-readable reason string (becomes the <c>text</c> field)</param>
    public AuthenticationFailedException(string detail) : base(detail) { }
}
