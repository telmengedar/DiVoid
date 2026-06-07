using System.Security.Claims;
using Backend.Errors.Exceptions;

namespace Backend.Auth;

/// <summary>
/// extension methods for <see cref="ClaimsPrincipal"/> that encapsulate
/// DiVoid-specific claim reading conventions
/// </summary>
public static class ClaimsExtensions {

    /// <summary>
    /// the distinct claim type emitted by both authentication schemes to carry
    /// the DiVoid row id of the authenticated user.
    ///
    /// <see cref="KeycloakClaimsTransformation"/> emits this in the augmentation
    /// identity it adds to JWT principals (instead of a second <c>NameIdentifier</c>).
    /// <see cref="Backend.Auth.ApiKeyAuthenticationHandler"/> emits this alongside
    /// <c>ClaimTypes.NameIdentifier</c> in the API-key identity.
    ///
    /// Using a distinct claim type removes the ambiguity that existed when two
    /// <c>NameIdentifier</c> claims with different semantics were present on the same
    /// JWT principal (the JWT <c>sub</c> and the DiVoid user id).
    /// </summary>
    public const string DivoidUserIdClaimType = "divoid.user_id";

    /// <summary>
    /// claim carrying the caller's accessible organization-ids as a CSV of longs;
    /// absence = admin-equivalent (no org filter). See organizations.md §8.
    /// </summary>
    public const string OrganizationIdsClaimType = "divoid.organization_ids";

    /// <summary>
    /// parses the org-ids CSV claim; null = absent (admin-equivalent), empty array = present-but-empty (zero memberships).
    /// </summary>
    /// <param name="principal">authenticated principal from the current request</param>
    /// <returns>parsed org-ids or null when the claim is absent</returns>
    public static long[] GetAccessibleOrgs(this ClaimsPrincipal principal)
    {
        string raw = principal.FindFirstValue(OrganizationIdsClaimType);
        if (raw == null) return null;
        if (raw.Length == 0) return [];
        string[] parts = raw.Split(',', System.StringSplitOptions.RemoveEmptyEntries);
        long[] ids = new long[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            ids[i] = long.Parse(parts[i]);
        return ids;
    }


    /// <summary>
    /// extracts the DiVoid user id from the principal's claims.
    ///
    /// Both authentication schemes emit a <c>divoid.user_id</c> claim with the
    /// numeric DiVoid row id.  Reading that claim directly is unambiguous regardless
    /// of how many <c>NameIdentifier</c> claims are present.
    /// </summary>
    /// <param name="principal">authenticated principal from the current request</param>
    /// <returns>DiVoid user id of the authenticated principal</returns>
    /// <exception cref="AuthorizationFailedException">
    /// thrown when the <c>divoid.user_id</c> claim is absent, which happens when
    /// the JWT transformation skipped augmentation (e.g. unknown or disabled user)
    /// </exception>
    public static long GetDivoidUserId(this ClaimsPrincipal principal) {
        string value = principal.FindFirstValue(DivoidUserIdClaimType);
        if (value != null && long.TryParse(value, out long parsed))
            return parsed;
        throw new AuthorizationFailedException("Principal does not carry a valid user identity");
    }
}
