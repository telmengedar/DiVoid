using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Pooshit.AspNetCore.Services.Errors;
using Pooshit.AspNetCore.Services.Errors.Exceptions;
using Pooshit.AspNetCore.Services.Errors.Handlers;

namespace Backend.Errors;

/// <summary>
/// maps <see cref="PropertyNotFoundException"/> to HTTP 400 Bad Request.
///
/// Pooshit's default mapping for this exception is 404, which is wrong for PATCH
/// path resolution: a bad patch path (e.g. <c>/type</c> when the entity stores
/// <c>TypeId</c>) is a client input error, not a missing resource.
///
/// This handler overrides the default for all call sites in this project.
/// The only such call site is <see cref="Extensions.DatabasePatchExtensions.Patch{T}"/>,
/// which throws when a PATCH path does not resolve to any property on the target entity.
/// </summary>
public class PropertyNotFoundExceptionHandler : ErrorHandler<PropertyNotFoundException> {

    /// <summary>
    /// creates a new <see cref="PropertyNotFoundExceptionHandler"/>
    /// </summary>
    /// <param name="logger">access to logging</param>
    public PropertyNotFoundExceptionHandler(ILogger<PropertyNotFoundExceptionHandler> logger) : base(logger) {
    }

    /// <inheritdoc />
    protected override HttpStatusCode HttpStatus(PropertyNotFoundException exception) => HttpStatusCode.BadRequest;

    /// <inheritdoc />
    protected override Task<ErrorResponse> GenerateResponse(PropertyNotFoundException exception, HttpContext context) {
        return Task.FromResult(new ErrorResponse(DefaultErrorCodes.BadParameter, exception.Message));
    }
}
