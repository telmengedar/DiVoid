using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Pooshit.AspNetCore.Services.Errors;
using Pooshit.AspNetCore.Services.Errors.Handlers;

namespace Backend.Errors;

/// <summary>
/// maps <see cref="NotSupportedException"/> to HTTP 400 Bad Request.
///
/// In this project the only source of <see cref="NotSupportedException"/> on the
/// request path is <see cref="Extensions.DatabasePatchExtensions.Patch{T}"/>, which
/// throws when a PATCH path resolves to a property that is not tagged
/// <c>[AllowPatch]</c>.
/// </summary>
public class NotSupportedExceptionHandler : ErrorHandler<NotSupportedException> {

    /// <summary>
    /// creates a new <see cref="NotSupportedExceptionHandler"/>
    /// </summary>
    /// <param name="logger">access to logging</param>
    public NotSupportedExceptionHandler(ILogger<NotSupportedExceptionHandler> logger) : base(logger) {
    }

    /// <inheritdoc />
    protected override HttpStatusCode HttpStatus(NotSupportedException exception) => HttpStatusCode.BadRequest;

    /// <inheritdoc />
    protected override Task<ErrorResponse> GenerateResponse(NotSupportedException exception, HttpContext context) {
        return Task.FromResult(new ErrorResponse(DefaultErrorCodes.BadParameter, exception.Message));
    }
}
