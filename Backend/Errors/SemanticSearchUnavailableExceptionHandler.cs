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
///
/// This handler is deliberately narrow: it only catches the one exception type
/// thrown when the caller uses <c>?query=</c> or <c>?minSimilarity=</c> on a
/// SQLite deployment.  Pre-existing <see cref="System.InvalidOperationException"/>
/// throws (e.g. LinkNodes self-link guard) are unaffected and continue to surface
/// as unhandled-path errors.
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
