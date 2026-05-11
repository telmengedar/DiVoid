using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Pooshit.AspNetCore.Services.Errors;
using Pooshit.AspNetCore.Services.Errors.Handlers;

namespace Backend.Errors;

/// <summary>
/// maps <see cref="InvalidOperationException"/> to HTTP 400 Bad Request with
/// <c>code=badparameter</c>.
///
/// <see cref="InvalidOperationException"/> is thrown on the request path when the
/// caller provides a combination of parameters that is logically invalid — for example,
/// sending <c>?query=...</c> on a deployment that does not support the
/// <c>embedding()</c> function, or supplying <c>minSimilarity</c> without
/// a <c>query</c>.
/// </summary>
public class InvalidOperationExceptionHandler : ErrorHandler<InvalidOperationException> {

    /// <summary>
    /// creates a new <see cref="InvalidOperationExceptionHandler"/>
    /// </summary>
    /// <param name="logger">access to logging</param>
    public InvalidOperationExceptionHandler(ILogger<InvalidOperationExceptionHandler> logger) : base(logger) {
    }

    /// <inheritdoc />
    protected override HttpStatusCode HttpStatus(InvalidOperationException exception) => HttpStatusCode.BadRequest;

    /// <inheritdoc />
    protected override Task<ErrorResponse> GenerateResponse(InvalidOperationException exception, HttpContext context) {
        return Task.FromResult(new ErrorResponse(DefaultErrorCodes.BadParameter, exception.Message));
    }
}
