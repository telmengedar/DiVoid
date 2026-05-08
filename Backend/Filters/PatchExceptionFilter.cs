using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Pooshit.AspNetCore.Services.Errors;
using Pooshit.AspNetCore.Services.Errors.Exceptions;

namespace Backend.Filters;

/// <summary>
/// Translates patch-input exceptions thrown by <see cref="Extensions.DatabasePatchExtensions"/>
/// into HTTP 400 responses with the standard <see cref="ErrorResponse"/> body shape.
///
/// <list type="bullet">
///   <item><see cref="PropertyNotFoundException"/> — the patch path does not resolve to any
///   property on the target entity (e.g. PATCH /type when the entity stores TypeId).</item>
///   <item><see cref="NotSupportedException"/> — the property exists but is not tagged
///   <c>[AllowPatch]</c> (e.g. PATCH /typeid on Node).</item>
/// </list>
/// </summary>
public class PatchExceptionFilter : IExceptionFilter {

    /// <inheritdoc/>
    public void OnException(ExceptionContext context) {
        ErrorResponse response = context.Exception switch {
            PropertyNotFoundException e => new ErrorResponse {
                Code = DefaultErrorCodes.BadParameter,
                Text = e.Message
            },
            NotSupportedException e => new ErrorResponse {
                Code = DefaultErrorCodes.BadParameter,
                Text = e.Message
            },
            _ => null
        };

        if (response == null)
            return;

        context.Result = new ObjectResult(response) {
            StatusCode = (int) HttpStatusCode.BadRequest
        };
        context.ExceptionHandled = true;
    }
}
