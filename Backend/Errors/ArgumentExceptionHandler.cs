using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Pooshit.AspNetCore.Services.Errors;
using Pooshit.AspNetCore.Services.Errors.Handlers;

namespace Backend.Errors;

/// <summary>
/// maps <see cref="ArgumentException"/> to HTTP 400 Bad Request.
///
/// <see cref="ArgumentException"/> is thrown by
/// <see cref="Extensions.DatabasePatchExtensions.ResolveJsonColumnValue"/> when a caller
/// supplies a value of the wrong type for a <c>[JsonColumn]</c> property
/// (e.g. a number or nested object instead of a string array or pre-encoded string).
/// </summary>
public class ArgumentExceptionHandler : ErrorHandler<ArgumentException> {

    /// <summary>
    /// creates a new <see cref="ArgumentExceptionHandler"/>
    /// </summary>
    /// <param name="logger">access to logging</param>
    public ArgumentExceptionHandler(ILogger<ArgumentExceptionHandler> logger) : base(logger) {
    }

    /// <inheritdoc />
    protected override HttpStatusCode HttpStatus(ArgumentException exception) => HttpStatusCode.BadRequest;

    /// <inheritdoc />
    protected override Task<ErrorResponse> GenerateResponse(ArgumentException exception, HttpContext context) {
        return Task.FromResult(new ErrorResponse(DefaultErrorCodes.BadParameter, exception.Message));
    }
}
