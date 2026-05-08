using System.Net;
using Backend.Query;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Pooshit.AspNetCore.Services.Errors;
using Pooshit.AspNetCore.Services.Errors.Handlers;

namespace Backend.Errors;

/// <summary>
/// maps <see cref="PathQueryParseException"/> to HTTP 400 Bad Request with
/// <c>code=badparameter</c>.
///
/// Parser errors are client input errors: the path query string is syntactically
/// invalid or uses an operator reserved for a future version of the API.
/// </summary>
public class PathQueryParseExceptionHandler : ErrorHandler<PathQueryParseException>
{
    /// <summary>
    /// creates a new <see cref="PathQueryParseExceptionHandler"/>
    /// </summary>
    /// <param name="logger">access to logging</param>
    public PathQueryParseExceptionHandler(ILogger<PathQueryParseExceptionHandler> logger) : base(logger) { }

    /// <inheritdoc />
    protected override HttpStatusCode HttpStatus(PathQueryParseException exception) => HttpStatusCode.BadRequest;

    /// <inheritdoc />
    protected override Task<ErrorResponse> GenerateResponse(PathQueryParseException exception, HttpContext context)
    {
        return Task.FromResult(new ErrorResponse(DefaultErrorCodes.BadParameter, exception.Message));
    }
}
