using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Pooshit.AspNetCore.Services.Errors;
using Pooshit.AspNetCore.Services.Errors.Handlers;
using Pooshit.Ocelot.Errors;

namespace Backend.Errors;

/// <summary>
/// maps <see cref="UnknownFieldException"/> to HTTP 400 Bad Request.
/// </summary>
public class UnknownFieldExceptionHandler : ErrorHandler<UnknownFieldException> {

    /// <summary>
    /// creates a new <see cref="UnknownFieldExceptionHandler"/>
    /// </summary>
    /// <param name="logger">access to logging</param>
    public UnknownFieldExceptionHandler(ILogger<UnknownFieldExceptionHandler> logger) : base(logger) {
    }

    /// <inheritdoc />
    protected override HttpStatusCode HttpStatus(UnknownFieldException exception) => HttpStatusCode.BadRequest;

    /// <inheritdoc />
    protected override Task<ErrorResponse> GenerateResponse(UnknownFieldException ex, HttpContext context) {
        return Task.FromResult(new ErrorResponse(DefaultErrorCodes.BadParameter, ex.Message,
            new UnknownFieldContext { Field = ex.FieldName, Available = ex.AvailableNames }));
    }

    /// <inheritdoc />
    protected override void LogError(ILogger errorlogger, UnknownFieldException error, HttpContext context) {
        errorlogger.LogWarning("Unknown field '{Field}' requested", error.FieldName);
    }
}
