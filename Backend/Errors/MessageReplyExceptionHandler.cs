using System.Net;
using Backend.Errors.Exceptions;
using Pooshit.AspNetCore.Services.Errors;
using Pooshit.AspNetCore.Services.Errors.Handlers;

namespace Backend.Errors;

/// <summary>Maps explicit external reply failures to safe public errors.</summary>
public class MessageReplyExceptionHandler : ErrorHandler<MessageReplyException> {
    /// <summary>Creates the reply failure handler.</summary>
    /// <param name="logger">The application logger.</param>
    public MessageReplyExceptionHandler(ILogger<MessageReplyExceptionHandler> logger) : base(logger) { }

    /// <inheritdoc />
    protected override HttpStatusCode HttpStatus(MessageReplyException exception) => exception.Status;

    /// <inheritdoc />
    protected override Task<ErrorResponse> GenerateResponse(MessageReplyException exception, HttpContext context) =>
        Task.FromResult(new ErrorResponse(exception.Code, exception.Message));
}
