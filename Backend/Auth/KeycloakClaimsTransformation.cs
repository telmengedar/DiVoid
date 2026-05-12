using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Backend.Models.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pooshit.AspNetCore.Services.Errors.Exceptions;
using Pooshit.Json;
using Pooshit.Ocelot.Entities;

namespace Backend.Auth;

/// <summary>
/// normalises a Keycloak-issued JWT principal into the same claim shape
/// that <see cref="ApiKeyAuthenticationHandler"/> produces, so that the
/// existing <see cref="PermissionAuthorizationHandler"/> works without changes.
///
/// Only principals authenticated via the JwtBearer scheme are touched;
/// API-key principals pass through unchanged.
/// </summary>
public class KeycloakClaimsTransformation : IClaimsTransformation {
    readonly IEntityManager database;
    readonly string userIdClaimName;
    readonly ILogger<KeycloakClaimsTransformation> logger;

    const string AugmentedMarker = "divoid.augmented";

    /// <summary>
    /// creates a new <see cref="KeycloakClaimsTransformation"/>
    /// </summary>
    /// <param name="database">access to database</param>
    /// <param name="configuration">application configuration</param>
    /// <param name="logger">logger</param>
    public KeycloakClaimsTransformation(
        IEntityManager database,
        IConfiguration configuration,
        ILogger<KeycloakClaimsTransformation> logger) {
        this.database = database;
        this.logger = logger;
        userIdClaimName = configuration["Keycloak:UserIdClaimName"] ?? "userId";
    }

    /// <inheritdoc />
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal) {
        // Idempotency guard — avoid double-augmentation on retried requests
        if (principal.HasClaim(AugmentedMarker, "1"))
            return principal;

        // Only process JWT principals (they carry an 'iss' claim; API-key principals do not).
        // We avoid relying on AuthenticationType because the JsonWebTokenHandler used by
        // JwtBearer in .NET 9 may produce identities whose AuthenticationType differs
        // from JwtBearerDefaults.AuthenticationScheme ("Bearer") in some host configurations.
        // The presence of 'iss' is a reliable discriminator: it is always present in a
        // validated Keycloak JWT and never present in an API-key principal.
        if (!principal.HasClaim(c => c.Type == "iss"))
            return principal;

        string userIdClaim = principal.FindFirstValue(userIdClaimName);
        if (string.IsNullOrEmpty(userIdClaim)) {
            logger.LogWarning("event=auth.jwt.no_user_id");
            return principal;
        }

        if (!long.TryParse(userIdClaim, out long userId)) {
            logger.LogWarning("event=auth.jwt.no_user_id");
            return principal;
        }

        User user;
        try {
            user = await database.Load<User>()
                                 .Where(u => u.Id == userId)
                                 .ExecuteEntityAsync();
        } catch (Exception ex) {
            logger.LogWarning(ex, "event=auth.jwt.unknown_user userId={UserId}", userId);
            return principal;
        }

        if (user == null) {
            logger.LogWarning("event=auth.jwt.unknown_user userId={UserId}", userId);
            return principal;
        }

        if (!user.Enabled) {
            logger.LogWarning("event=auth.jwt.disabled_user userId={UserId}", userId);
            return principal;
        }

        // Build a new identity with DiVoid claims layered on top of the Keycloak ones
        ClaimsIdentity identity = new(JwtBearerDefaults.AuthenticationScheme);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        identity.AddClaim(new Claim(AugmentedMarker, "1"));

        string[] permissions = string.IsNullOrEmpty(user.Permissions)
            ? []
            : Json.Read<string[]>(user.Permissions) ?? [];

        foreach (string permission in permissions)
            identity.AddClaim(new Claim("permission", permission));

        logger.LogInformation("event=auth.jwt.success userId={UserId}", userId);

        ClaimsPrincipal augmented = new(principal);
        augmented.AddIdentity(identity);
        return augmented;
    }
}
