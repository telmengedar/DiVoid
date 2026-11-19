using System.Net;

namespace Backend.Errors.Exceptions;

/// <summary>A fixed public failure of graph change-stream registration.</summary>
public sealed class NodeChangeException : Exception {
    /// <summary>Creates the registration failure.</summary>
    /// <param name="status">The public response status.</param>
    /// <param name="code">The fixed public error code.</param>
    /// <param name="message">The safe public explanation.</param>
    public NodeChangeException(HttpStatusCode status, string code, string message) : base(message) {
        Status = status;
        Code = code;
    }

    /// <summary>The public response status.</summary>
    public HttpStatusCode Status { get; }

    /// <summary>The fixed public error code.</summary>
    public string Code { get; }
}
