using System.Security.Claims;
using Backend.Errors.Exceptions;

namespace Backend.Auth;

/// <summary>
/// extension methods for <see cref="ClaimsPrincipal"/> that encapsulate
/// DiVoid-specific claim reading conventions
/// </summary>
public static class ClaimsExtensions {

    /// <summary>
    /// extracts the DiVoid user id from the principal's claims.
    ///
    /// For JWT principals, <see cref="KeycloakClaimsTransformation"/> adds a second identity
    /// whose <c>NameIdentifier</c> is the numeric DiVoid user id.  The original Keycloak
    /// identity carries a UUID-shaped <c>sub</c> mapped to <c>NameIdentifier</c> as well.
    /// Scanning all <c>NameIdentifier</c> claims and returning the first one that parses as
    /// a <c>long</c> reliably picks the DiVoid id regardless of claim order.
    ///
    /// For API-key principals there is only one identity and its <c>NameIdentifier</c> is
    /// already numeric, so the scan terminates on the first (and only) value.
    /// </summary>
    /// <param name="principal">authenticated principal from the current request</param>
    /// <returns>DiVoid user id of the authenticated principal</returns>
    /// <exception cref="AuthorizationFailedException">
    /// thrown when no numeric <c>NameIdentifier</c> claim is present — which happens when
    /// the JWT transformation skipped augmentation (e.g. unknown or disabled user)
    /// </exception>
    public static long GetDivoidUserId(this ClaimsPrincipal principal) {
        foreach (Claim claim in principal.FindAll(ClaimTypes.NameIdentifier)) {
            if (long.TryParse(claim.Value, out long parsed))
                return parsed;
        }
        throw new AuthorizationFailedException("Principal does not carry a valid user identity");
    }
}
