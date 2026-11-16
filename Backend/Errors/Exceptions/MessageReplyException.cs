using System.Net;

namespace Backend.Errors.Exceptions;

/// <summary>A non-sensitive external reply or binding failure.</summary>
public class MessageReplyException : Exception {
    /// <summary>Creates an explicit integration failure.</summary>
    /// <param name="status">The public HTTP status.</param>
    /// <param name="code">The stable error category.</param>
    /// <param name="text">The non-sensitive public explanation.</param>
    public MessageReplyException(HttpStatusCode status, string code, string text) : base(text) {
        Status = status;
        Code = code;
    }

    /// <summary>The public HTTP status.</summary>
    public HttpStatusCode Status { get; }

    /// <summary>The stable public error category.</summary>
    public string Code { get; }
}
