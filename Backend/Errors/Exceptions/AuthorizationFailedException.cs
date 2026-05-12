namespace Backend.Errors.Exceptions;

/// <summary>
/// thrown by auth handlers when a request is authenticated but lacks the required
/// permission (HTTP 403).
/// the <see cref="System.Exception.Message"/> becomes the <c>text</c> field in the
/// canonical <c>{ code, text }</c> error response produced by
/// <see cref="AuthorizationFailedExceptionHandler"/>.
/// </summary>
public class AuthorizationFailedException : Exception {
    /// <summary>
    /// creates a new <see cref="AuthorizationFailedException"/> with a human-readable detail
    /// </summary>
    /// <param name="detail">human-readable reason string (becomes the <c>text</c> field)</param>
    public AuthorizationFailedException(string detail) : base(detail) { }
}
