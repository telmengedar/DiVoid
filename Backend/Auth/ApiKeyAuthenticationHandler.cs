using System;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Backend.Services.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pooshit.AspNetCore.Services.Errors.Exceptions;

namespace Backend.Auth;

/// <summary>
/// authentication handler that validates API keys supplied as Bearer tokens
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions> {
    readonly IApiKeyService apiKeyService;
    readonly ILogger<ApiKeyAuthenticationHandler> logger;

    /// <summary>
    /// name of the custom authentication scheme
    /// </summary>
    public const string SchemeName = "ApiKey";

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder,
        IApiKeyService apiKeyService)
        : base(options, loggerFactory, encoder) {
        this.apiKeyService = apiKeyService;
        this.logger = loggerFactory.CreateLogger<ApiKeyAuthenticationHandler>();
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync() {
        string authHeader = Request.Headers["Authorization"];
        if (string.IsNullOrEmpty(authHeader))
            return AuthenticateResult.NoResult();

        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) {
            logger.LogWarning("event=auth.failed reason=missing_header path={Path}", Request.Path);
            return AuthenticateResult.NoResult();
        }

        string key = authHeader.Substring("Bearer ".Length).Trim();
        if (string.IsNullOrEmpty(key)) {
            logger.LogWarning("event=auth.failed reason=missing_header path={Path}", Request.Path);
            return AuthenticateResult.NoResult();
        }

        try {
            Models.Auth.ApiKeyDetails details = await apiKeyService.GetApiKey(key);

            ClaimsIdentity identity = new(SchemeName);
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, details.UserId?.ToString() ?? "0"));
            foreach (string permission in details.Permissions ?? [])
                identity.AddClaim(new Claim("permission", permission));

            ClaimsPrincipal principal = new(identity);
            AuthenticationTicket ticket = new(principal, SchemeName);
            return AuthenticateResult.Success(ticket);
        } catch (InvalidOperationException ex) when (ex.Message is "disabled_key" or "expired" or "disabled_user") {
            string reason = ex.Message;
            logger.LogWarning("event=auth.failed reason={Reason} path={Path} clientIp={ClientIp}",
                reason, Request.Path, Request.HttpContext.Connection.RemoteIpAddress);
            return AuthenticateResult.Fail(reason);
        } catch (NotFoundException<Models.Auth.ApiKey>) {
            logger.LogWarning("event=auth.failed reason=invalid_key path={Path} clientIp={ClientIp}",
                Request.Path, Request.HttpContext.Connection.RemoteIpAddress);
            return AuthenticateResult.Fail("invalid api key");
        } catch (Exception ex) {
            logger.LogWarning(ex, "event=auth.failed reason=invalid_key path={Path}", Request.Path);
            return AuthenticateResult.Fail("invalid api key");
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) {
        Response.Headers["WWW-Authenticate"] = "Bearer";
        Response.StatusCode = 401;
        return Task.CompletedTask;
    }
}
