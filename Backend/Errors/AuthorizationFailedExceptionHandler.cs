using System.Net;
using Backend.Errors.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Pooshit.AspNetCore.Services.Errors;
using Pooshit.AspNetCore.Services.Errors.Handlers;

namespace Backend.Errors;

/// <summary>
/// maps <see cref="AuthorizationFailedException"/> to HTTP 403 Forbidden with
/// <c>code=authorization_missingscope</c>.
///
/// sits in the canonical <see cref="Pooshit.AspNetCore.Services.Middleware.ErrorHandlerMiddleware"/>
/// pipeline and produces the same <c>{ code, text }</c> shape as every other 4xx in the project.
/// </summary>
public class AuthorizationFailedExceptionHandler : ErrorHandler<AuthorizationFailedException> {
    /// <summary>
    /// creates a new <see cref="AuthorizationFailedExceptionHandler"/>
    /// </summary>
    /// <param name="logger">access to logging</param>
    public AuthorizationFailedExceptionHandler(ILogger<AuthorizationFailedExceptionHandler> logger) : base(logger) { }

    /// <inheritdoc />
    protected override HttpStatusCode HttpStatus(AuthorizationFailedException exception) => HttpStatusCode.Forbidden;

    /// <inheritdoc />
    protected override Task<ErrorResponse> GenerateResponse(AuthorizationFailedException exception, HttpContext context) =>
        Task.FromResult(new ErrorResponse(DefaultErrorCodes.MissingScope, exception.Message));
}
