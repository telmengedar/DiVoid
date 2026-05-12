using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Backend.Errors.Exceptions;
using Backend.Services.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
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

        // Symmetric guard: if the bearer value looks like a JWT (exactly 2 dots -
        // compact serialization is header.payload.signature), abstain immediately.
        // JwtBearer has already had its chance; falling through and failing a DB lookup
        // would log a misleading "invalid api key" error when the real issue is on the
        // JWT path (wrong audience, expired token, bad signature, JWKS unreachable, etc.).
        // This mirrors the JwtBearerEvents.OnMessageReceived dot-count gate in Startup.cs.
        int dotCount = 0;
        foreach (char c in key) {
            if (c == '.') dotCount++;
        }
        if (dotCount == 2)
            return AuthenticateResult.NoResult();

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
            Context.Items["divoid.auth.failure_reason"] = reason;
            return AuthenticateResult.Fail(reason);
        } catch (NotFoundException<Models.Auth.ApiKey>) {
            logger.LogWarning("event=auth.failed reason=invalid_key path={Path} clientIp={ClientIp}",
                Request.Path, Request.HttpContext.Connection.RemoteIpAddress);
            Context.Items["divoid.auth.failure_reason"] = "invalid_key";
            return AuthenticateResult.Fail("invalid api key");
        } catch (Exception ex) {
            logger.LogWarning(ex, "event=auth.failed reason=invalid_key path={Path}", Request.Path);
            Context.Items["divoid.auth.failure_reason"] = "invalid_key";
            return AuthenticateResult.Fail("invalid api key");
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) {
        // Throwing here propagates through AuthorizationMiddleware up to ErrorHandlerMiddleware.
        // ErrorHandlerMiddleware produces the canonical { code, text } body at HTTP 401 with
        // a WWW-Authenticate: Bearer header (set by AuthenticationFailedExceptionHandler).
        throw new AuthenticationFailedException(ResolveApiKeyFailureDetail());
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) {
        throw new AuthorizationFailedException(BuildForbiddenDetail());
    }

    string ResolveApiKeyFailureDetail() {
        // Check for a missing or malformed Authorization header first (these cases
        // never reach HandleAuthenticateAsync, so no failure_reason is stored).
        string authHeader = Request.Headers["Authorization"];
        if (string.IsNullOrEmpty(authHeader))
            return "Authorization header missing";
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return "Authorization header must use Bearer scheme";

        // Failure reason was stashed by HandleAuthenticateAsync.
        string reason = Context.Items["divoid.auth.failure_reason"] as string ?? "invalid_key";
        return MapApiKeyFailureToDetail(reason);
    }

    string BuildForbiddenDetail() {
        // Walk the endpoint metadata to find the first named policy (PermissionRequirement name).
        Microsoft.AspNetCore.Http.Endpoint endpoint = Context.GetEndpoint();
        if (endpoint != null) {
            IAuthorizeData[] authorizeData = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ToArray();
            if (authorizeData.Length > 0) {
                string policyName = authorizeData[0].Policy;
                if (!string.IsNullOrEmpty(policyName))
                    return $"Caller lacks required permission '{policyName}'";
            }
        }

        return "Caller lacks required permission";
    }

    static string MapApiKeyFailureToDetail(string reason) => reason switch {
        "disabled_key"  => "API key is disabled",
        "expired"       => "API key has expired",
        "disabled_user" => "DiVoid account is disabled",
        _               => "API key not recognised"
    };
}
