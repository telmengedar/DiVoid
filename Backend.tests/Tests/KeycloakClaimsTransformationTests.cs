#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Backend.Auth;
using Backend.Models.Users;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Pooshit.Ocelot.Entities;
using Backend.tests.Fixtures;

namespace Backend.tests.Tests;

/// <summary>
/// Unit tests for KeycloakClaimsTransformation claim-emission shape (DiVoid #240).
/// After the refactor, the transformation emits divoid.user_id instead of a second NameIdentifier.
/// Substitution probe: remove the divoid.user_id AddClaim call in KeycloakClaimsTransformation
/// and the count-is-1 tests fail, confirming they are load-bearing per #275.
/// </summary>
[TestFixture, Parallelizable]
public class KeycloakClaimsTransformationTests {

    static IConfiguration BuildConfig(string userIdClaimName = "userId") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Keycloak:UserIdClaimName"] = userIdClaimName
            })
            .Build();

    static ClaimsPrincipal BuildJwtPrincipal(string sub, long? divoidUserId = null) {
        ClaimsIdentity identity = new("Bearer");
        identity.AddClaim(new Claim("iss", "https://test-keycloak.local/realms/master"));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, sub));
        if (divoidUserId.HasValue)
            identity.AddClaim(new Claim("userId", divoidUserId.Value.ToString()));
        return new ClaimsPrincipal(identity);
    }

    static async Task<long> InsertEnabledUserAsync(IEntityManager db, string name) {
        return await db.Insert<User>()
                       .Columns(u => u.Name, u => u.Email, u => u.Enabled, u => u.Permissions, u => u.CreatedAt)
                       .Values(name, name + "@test.com", true, null, DateTime.UtcNow)
                       .ReturnID()
                       .ExecuteAsync();
    }


    [Test, Parallelizable]
    public async Task TransformAsync_EnabledUser_EmitsExactlyOneDivoidUserIdClaim() {
        using DatabaseFixture db = new();
        long userId = await InsertEnabledUserAsync(db.EntityManager, "transform-emit");
        KeycloakClaimsTransformation t = new(db.EntityManager, BuildConfig(), NullLogger<KeycloakClaimsTransformation>.Instance);
        ClaimsPrincipal principal = BuildJwtPrincipal(sub: "some-sub", divoidUserId: userId);
        ClaimsPrincipal augmented = await t.TransformAsync(principal);
        IEnumerable<Claim> claims = augmented.FindAll(ClaimsExtensions.DivoidUserIdClaimType);
        Assert.That(claims, Has.Exactly(1).Items,
            "Transformation must emit exactly one divoid.user_id claim");
        Assert.That(claims.First().Value, Is.EqualTo(userId.ToString()),
            "divoid.user_id must carry the DiVoid row id");
    }


    [Test, Parallelizable]
    public async Task TransformAsync_EnabledUser_DoesNotAddExtraNameIdentifierClaim() {
        using DatabaseFixture db = new();
        long userId = await InsertEnabledUserAsync(db.EntityManager, "transform-no-ni");
        KeycloakClaimsTransformation t = new(db.EntityManager, BuildConfig(), NullLogger<KeycloakClaimsTransformation>.Instance);
        string originalSub = "original-sub-uuid";
        ClaimsPrincipal principal = BuildJwtPrincipal(sub: originalSub, divoidUserId: userId);
        ClaimsPrincipal augmented = await t.TransformAsync(principal);
        IEnumerable<Claim> nameIdClaims = augmented.FindAll(ClaimTypes.NameIdentifier);
        Assert.That(nameIdClaims, Has.Exactly(1).Items,
            "Transformation must not add a second NameIdentifier");
        Assert.That(nameIdClaims.First().Value, Is.EqualTo(originalSub),
            "The sole NameIdentifier must be the original Keycloak sub");
    }


    [Test, Parallelizable]
    public async Task TransformAsync_NumericSub_DivoidUserIdClaimCarriesCorrectId() {
        using DatabaseFixture db = new();
        long userId = await InsertEnabledUserAsync(db.EntityManager, "transform-numeric-sub");
        KeycloakClaimsTransformation t = new(db.EntityManager, BuildConfig(), NullLogger<KeycloakClaimsTransformation>.Instance);
        ClaimsPrincipal principal = BuildJwtPrincipal(sub: "42", divoidUserId: userId);
        ClaimsPrincipal augmented = await t.TransformAsync(principal);
        IEnumerable<Claim> claims = augmented.FindAll(ClaimsExtensions.DivoidUserIdClaimType);
        Assert.That(claims, Has.Exactly(1).Items,
            "divoid.user_id must be present even when sub is numeric");
        Assert.That(claims.First().Value, Is.EqualTo(userId.ToString()),
            "divoid.user_id must carry the row id, not the numeric sub");
    }


    [Test, Parallelizable]
    public async Task TransformAsync_CalledTwice_DoesNotDuplicateDivoidUserIdClaim() {
        using DatabaseFixture db = new();
        long userId = await InsertEnabledUserAsync(db.EntityManager, "transform-idempotent");
        KeycloakClaimsTransformation t = new(db.EntityManager, BuildConfig(), NullLogger<KeycloakClaimsTransformation>.Instance);
        ClaimsPrincipal principal = BuildJwtPrincipal(sub: "some-sub", divoidUserId: userId);
        ClaimsPrincipal first  = await t.TransformAsync(principal);
        ClaimsPrincipal second = await t.TransformAsync(first);
        IEnumerable<Claim> claims = second.FindAll(ClaimsExtensions.DivoidUserIdClaimType);
        Assert.That(claims, Has.Exactly(1).Items,
            "Calling transformation twice must not duplicate divoid.user_id");
    }


    [Test, Parallelizable]
    public async Task TransformAsync_ApiKeyPrincipal_PassesThroughUnchanged() {
        using DatabaseFixture db = new();
        KeycloakClaimsTransformation t = new(db.EntityManager, BuildConfig(), NullLogger<KeycloakClaimsTransformation>.Instance);
        ClaimsIdentity identity = new("ApiKey");
        identity.AddClaim(new Claim("divoid.user_id", "10"));
        ClaimsPrincipal principal = new(identity);
        ClaimsPrincipal result = await t.TransformAsync(principal);
        Assert.That(result.Identities.Count(), Is.EqualTo(1),
            "API-key principal (no iss claim) must pass through without additional identities");
    }


    [Test, Parallelizable]
    public async Task TransformAsync_DisabledUser_DoesNotEmitDivoidUserIdClaim() {
        using DatabaseFixture db = new();
        long userId = await db.EntityManager.Insert<User>()
                              .Columns(u => u.Name, u => u.Email, u => u.Enabled, u => u.Permissions, u => u.CreatedAt)
                              .Values("transform-disabled", "disabled@test.com", false, null, DateTime.UtcNow)
                              .ReturnID()
                              .ExecuteAsync();
        KeycloakClaimsTransformation t = new(db.EntityManager, BuildConfig(), NullLogger<KeycloakClaimsTransformation>.Instance);
        ClaimsPrincipal principal = BuildJwtPrincipal(sub: "some-sub", divoidUserId: userId);
        ClaimsPrincipal result = await t.TransformAsync(principal);
        IEnumerable<Claim> claims = result.FindAll(ClaimsExtensions.DivoidUserIdClaimType);
        Assert.That(claims, Is.Empty,
            "Disabled user: augmentation skipped, no divoid.user_id emitted");
    }
}
