using System.Net;
using Backend.Errors.Exceptions;
using Pooshit.AspNetCore.Services.Errors;
using Pooshit.AspNetCore.Services.Errors.Handlers;

namespace Backend.Errors;

/// <summary>Maps graph stream registration failures to fixed public errors.</summary>
/// <param name="logger">The ordinary error logger.</param>
public sealed class NodeChangeExceptionHandler(ILogger<NodeChangeExceptionHandler> logger) : ErrorHandler<NodeChangeException>(logger) {
    /// <inheritdoc />
    protected override HttpStatusCode HttpStatus(NodeChangeException exception) => exception.Status;

    /// <inheritdoc />
    protected override Task<ErrorResponse> GenerateResponse(NodeChangeException exception, HttpContext context) =>
        Task.FromResult(new ErrorResponse(exception.Code, exception.Message));
}
