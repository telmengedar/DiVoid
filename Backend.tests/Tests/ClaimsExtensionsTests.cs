#nullable enable
using System.Security.Claims;
using Backend.Auth;
using Backend.Errors.Exceptions;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// Unit tests for <see cref="ClaimsExtensions.GetDivoidUserId"/>.
///
/// After the DiVoid #240 refactor, GetDivoidUserId reads the distinct
/// "divoid.user_id" claim directly instead of scanning all NameIdentifier
/// claims for the first long-parseable value.
///
/// Substitution probe: comment out the "divoid.user_id" AddClaim call in
/// KeycloakClaimsTransformation (or ApiKeyAuthenticationHandler) - the
/// tests that assert a successful read will throw AuthorizationFailedException
/// and NUnit marks them as Fail, confirming the tests are load-bearing (#275).
/// </summary>
[TestFixture, Parallelizable]
public class ClaimsExtensionsTests {

    [Test, Parallelizable]
    public void GetDivoidUserId_WithDivoidUserIdClaim_ReturnsCorrectId() {
        ClaimsIdentity identity = new("ApiKey");
        identity.AddClaim(new Claim("divoid.user_id", "42"));
        ClaimsPrincipal principal = new(identity);

        long result = principal.GetDivoidUserId();

        Assert.That(result, Is.EqualTo(42L),
            "GetDivoidUserId must read the divoid.user_id claim directly");
    }

    [Test, Parallelizable]
    public void GetDivoidUserId_WithNumericNameIdentifierButNoDivoidUserIdClaim_Throws() {
        ClaimsIdentity identity = new("ApiKey");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "99"));
        ClaimsPrincipal principal = new(identity);

        Assert.Throws<AuthorizationFailedException>(() => principal.GetDivoidUserId(),
            "GetDivoidUserId must throw when only NameIdentifier is present - divoid.user_id is required");
    }

    [Test, Parallelizable]
    public void GetDivoidUserId_WithBothDivoidUserIdAndNumericNameIdentifier_ReturnsDivoidUserIdValue() {
        ClaimsIdentity identity = new("ApiKey");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "7"));
        identity.AddClaim(new Claim("divoid.user_id", "7"));
        ClaimsPrincipal principal = new(identity);

        long result = principal.GetDivoidUserId();

        Assert.That(result, Is.EqualTo(7L),
            "GetDivoidUserId must return the divoid.user_id value when both claims are present");
    }

    [Test, Parallelizable]
    public void GetDivoidUserId_JwtPrincipalShape_TwoIdentities_ReadsFromDivoidUserIdNotNameIdentifier() {
        ClaimsIdentity jwtIdentity = new("Bearer");
        jwtIdentity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "some-uuid-sub-not-parseable-as-long"));

        ClaimsIdentity augmentedIdentity = new("Bearer");
        augmentedIdentity.AddClaim(new Claim("divoid.user_id", "55"));
        augmentedIdentity.AddClaim(new Claim("divoid.augmented", "1"));

        ClaimsPrincipal principal = new(jwtIdentity);
        principal.AddIdentity(augmentedIdentity);

        long result = principal.GetDivoidUserId();

        Assert.That(result, Is.EqualTo(55L),
            "GetDivoidUserId must read divoid.user_id from the augmented identity, ignoring NameIdentifier on the JWT identity");
    }

    [Test, Parallelizable]
    public void GetDivoidUserId_JwtPrincipalShape_NumericSub_StillResolvesCorrectDivoidId() {
        // Regression for the old scan heuristic.
        ClaimsIdentity jwtIdentity = new("Bearer");
        jwtIdentity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "42")); // numeric sub

        ClaimsIdentity augmentedIdentity = new("Bearer");
        augmentedIdentity.AddClaim(new Claim("divoid.user_id", "100")); // actual DiVoid id
        augmentedIdentity.AddClaim(new Claim("divoid.augmented", "1"));

        ClaimsPrincipal principal = new(jwtIdentity);
        principal.AddIdentity(augmentedIdentity);

        long result = principal.GetDivoidUserId();

        Assert.That(result, Is.EqualTo(100L),
            "When JWT sub is numeric (42), GetDivoidUserId must return divoid.user_id (100), not the sub");
    }

    [Test, Parallelizable]
    public void GetDivoidUserId_NoClaims_Throws() {
        ClaimsPrincipal principal = new(new ClaimsIdentity());

        Assert.Throws<AuthorizationFailedException>(() => principal.GetDivoidUserId(),
            "GetDivoidUserId must throw AuthorizationFailedException when no divoid.user_id claim is present");
    }

    [Test, Parallelizable]
    public void GetDivoidUserId_DivoidUserIdClaimNonNumeric_Throws() {
        ClaimsIdentity identity = new("ApiKey");
        identity.AddClaim(new Claim("divoid.user_id", "not-a-number"));
        ClaimsPrincipal principal = new(identity);

        Assert.Throws<AuthorizationFailedException>(() => principal.GetDivoidUserId(),
            "GetDivoidUserId must throw when divoid.user_id is present but not parseable as long");
    }

    [Test, Parallelizable]
    public void DivoidUserIdClaimType_IsExpectedValue() {
        Assert.That(ClaimsExtensions.DivoidUserIdClaimType, Is.EqualTo("divoid.user_id"),
            "DivoidUserIdClaimType constant must be the canonical claim name");
    }
}
