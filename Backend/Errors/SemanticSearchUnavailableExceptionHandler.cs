using System.Net;
using Backend.Services.Embeddings;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Pooshit.AspNetCore.Services.Errors;
using Pooshit.AspNetCore.Services.Errors.Handlers;

namespace Backend.Errors;

/// <summary>
/// maps <see cref="SemanticSearchUnavailableException"/> to HTTP 400 Bad Request
/// with <c>code=badparameter</c>.
/// </summary>
public class SemanticSearchUnavailableExceptionHandler : ErrorHandler<SemanticSearchUnavailableException>
{
    /// <summary>
    /// creates a new <see cref="SemanticSearchUnavailableExceptionHandler"/>
    /// </summary>
    /// <param name="logger">access to logging</param>
    public SemanticSearchUnavailableExceptionHandler(ILogger<SemanticSearchUnavailableExceptionHandler> logger)
        : base(logger)
    {
    }

    /// <inheritdoc />
    protected override HttpStatusCode HttpStatus(SemanticSearchUnavailableException exception)
        => HttpStatusCode.BadRequest;

    /// <inheritdoc />
    protected override Task<ErrorResponse> GenerateResponse(SemanticSearchUnavailableException exception, HttpContext context)
        => Task.FromResult(new ErrorResponse(DefaultErrorCodes.BadParameter, exception.Message));
}
