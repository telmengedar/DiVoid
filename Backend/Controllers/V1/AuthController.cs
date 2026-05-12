using System.Security.Claims;
using System.Threading.Tasks;
using Backend.Models.Auth;
using Backend.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Backend.Controllers.V1;

/// <summary>
/// controller for authentication-related endpoints
/// </summary>
[Route("api/auth")]
[ApiController]
public class AuthController(ILogger<AuthController> logger, IAuthService authService) : ControllerBase {
    readonly ILogger<AuthController> logger = logger;
    readonly IAuthService authService = authService;

    /// <summary>
    /// returns the identity and permissions of the authenticated principal.
    /// Requires authentication via either a Keycloak JWT or an API key (both schemes
    /// are handled by the <c>DiVoidBearer</c> policy scheme). No specific permission
    /// is required — even a freshly provisioned user with no DiVoid permissions can
    /// call this endpoint so the frontend can discover its own permission set.
    ///
    /// For JWT-authenticated requests, <c>permissions</c> reflects the owning
    /// user's permission set. For API-key-authenticated requests, <c>permissions</c>
    /// reflects the key's own permission set, which may differ from the user's.
    /// </summary>
    /// <returns>identity and permissions of the authenticated principal</returns>
    [HttpGet("whoami")]
    [Authorize]
    public Task<WhoamiDetails> Whoami() {
        logger.LogInformation("event=auth.whoami userId={UserId}",
            User.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        return authService.GetWhoami(User);
    }
}
