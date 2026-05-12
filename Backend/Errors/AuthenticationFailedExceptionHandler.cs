using System.Net;
using Backend.Errors.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Pooshit.AspNetCore.Services.Errors;
using Pooshit.AspNetCore.Services.Errors.Handlers;

namespace Backend.Errors;

/// <summary>
/// maps <see cref="AuthenticationFailedException"/> to HTTP 401 Unauthorized with
/// <c>code=authorization_invalidtoken</c> and a <c>WWW-Authenticate: Bearer</c> header.
///
/// sits in the canonical <see cref="Pooshit.AspNetCore.Services.Middleware.ErrorHandlerMiddleware"/>
/// pipeline and produces the same <c>{ code, text }</c> shape as every other 4xx in the project.
/// </summary>
public class AuthenticationFailedExceptionHandler : ErrorHandler<AuthenticationFailedException> {
    /// <summary>
    /// creates a new <see cref="AuthenticationFailedExceptionHandler"/>
    /// </summary>
    /// <param name="logger">access to logging</param>
    public AuthenticationFailedExceptionHandler(ILogger<AuthenticationFailedExceptionHandler> logger) : base(logger) { }

    /// <inheritdoc />
    protected override HttpStatusCode HttpStatus(AuthenticationFailedException exception) => HttpStatusCode.Unauthorized;

    /// <inheritdoc />
    protected override Task<ErrorResponse> GenerateResponse(AuthenticationFailedException exception, HttpContext context) {
        context.Response.Headers["WWW-Authenticate"] = "Bearer";
        return Task.FromResult(new ErrorResponse(DefaultErrorCodes.InvalidToken, exception.Message));
    }
}
