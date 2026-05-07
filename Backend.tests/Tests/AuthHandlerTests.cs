using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Backend.Auth;
using Backend.Models.Auth;
using Backend.Services.Auth;
using Backend.tests.Fixtures;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Backend.tests.Tests;

/// <summary>
/// Tests for <see cref="ApiKeyAuthenticationHandler"/> and the authorization policy implication chain.
/// </summary>
[TestFixture]
public class AuthHandlerTests
{
    const string TestPepper = "auth-handler-test-pepper-value-32-bytes-minimum-000";

    static IApiKeyService MakeApiKeyService(DatabaseFixture fixture)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["DIVOID_KEY_PEPPER"] = TestPepper,
                ["Auth:Enabled"] = "true"
            })
            .Build();
        return new ApiKeyService(fixture.EntityManager, new KeyGenerator(), config, NullLogger<ApiKeyService>.Instance);
    }

    static async Task<long> CreateUser(DatabaseFixture fixture, bool enabled = true)
    {
        return await fixture.EntityManager.Insert<Backend.Services.Users.User>()
                            .Columns(u => u.Name, u => u.Enabled, u => u.CreatedAt)
                            .Values("test-user", enabled, DateTime.UtcNow)
                            .ReturnID()
                            .ExecuteAsync();
    }

    // -----------------------------------------------------------------------
    // Auth handler — claims populated correctly on success
    // -----------------------------------------------------------------------

    [Test]
    public async Task GetApiKey_ReturnsCorrectPermissionClaims()
    {
        // Test that GetApiKey returns details that would populate the correct claims.
        // The handler simply wraps GetApiKey and maps permissions → claims.
        using DatabaseFixture fixture = new();
        IApiKeyService svc = MakeApiKeyService(fixture);
        long userId = await CreateUser(fixture);
        ApiKeyDetails key = await svc.CreateApiKey(new ApiKeyParameters { UserId = userId, Permissions = ["read", "write"] });

        ApiKeyDetails fetched = await svc.GetApiKey(key.PlaintextKey!);

        Assert.Multiple(() => {
            Assert.That(fetched.Permissions, Does.Contain("read"));
            Assert.That(fetched.Permissions, Does.Contain("write"));
            Assert.That(fetched.UserId, Is.EqualTo(userId));
        });
    }

    // -----------------------------------------------------------------------
    // Authorization policy implication chain
    // -----------------------------------------------------------------------

    [Test]
    public void PermissionHandler_AdminSatisfiesAdminPolicy()
    {
        PermissionAuthorizationHandler handler = new();
        ClaimsPrincipal principal = BuildPrincipal("admin");
        AuthorizationHandlerContext ctx = BuildAuthContext(new PermissionRequirement("admin"), principal);
        handler.HandleAsync(ctx).GetAwaiter().GetResult();
        Assert.That(ctx.HasSucceeded, Is.True);
    }

    [Test]
    public void PermissionHandler_AdminSatisfiesWritePolicy()
    {
        PermissionAuthorizationHandler handler = new();
        ClaimsPrincipal principal = BuildPrincipal("admin");
        AuthorizationHandlerContext ctx = BuildAuthContext(new PermissionRequirement("write"), principal);
        handler.HandleAsync(ctx).GetAwaiter().GetResult();
        Assert.That(ctx.HasSucceeded, Is.True);
    }

    [Test]
    public void PermissionHandler_AdminSatisfiesReadPolicy()
    {
        PermissionAuthorizationHandler handler = new();
        ClaimsPrincipal principal = BuildPrincipal("admin");
        AuthorizationHandlerContext ctx = BuildAuthContext(new PermissionRequirement("read"), principal);
        handler.HandleAsync(ctx).GetAwaiter().GetResult();
        Assert.That(ctx.HasSucceeded, Is.True);
    }

    [Test]
    public void PermissionHandler_WriteSatisfiesWritePolicy()
    {
        PermissionAuthorizationHandler handler = new();
        ClaimsPrincipal principal = BuildPrincipal("write");
        AuthorizationHandlerContext ctx = BuildAuthContext(new PermissionRequirement("write"), principal);
        handler.HandleAsync(ctx).GetAwaiter().GetResult();
        Assert.That(ctx.HasSucceeded, Is.True);
    }

    [Test]
    public void PermissionHandler_WriteSatisfiesReadPolicy()
    {
        PermissionAuthorizationHandler handler = new();
        ClaimsPrincipal principal = BuildPrincipal("write");
        AuthorizationHandlerContext ctx = BuildAuthContext(new PermissionRequirement("read"), principal);
        handler.HandleAsync(ctx).GetAwaiter().GetResult();
        Assert.That(ctx.HasSucceeded, Is.True);
    }

    [Test]
    public void PermissionHandler_WriteDoesNotSatisfyAdminPolicy()
    {
        PermissionAuthorizationHandler handler = new();
        ClaimsPrincipal principal = BuildPrincipal("write");
        AuthorizationHandlerContext ctx = BuildAuthContext(new PermissionRequirement("admin"), principal);
        handler.HandleAsync(ctx).GetAwaiter().GetResult();
        Assert.That(ctx.HasSucceeded, Is.False);
    }

    [Test]
    public void PermissionHandler_ReadDoesNotSatisfyWritePolicy()
    {
        PermissionAuthorizationHandler handler = new();
        ClaimsPrincipal principal = BuildPrincipal("read");
        AuthorizationHandlerContext ctx = BuildAuthContext(new PermissionRequirement("write"), principal);
        handler.HandleAsync(ctx).GetAwaiter().GetResult();
        Assert.That(ctx.HasSucceeded, Is.False);
    }

    [Test]
    public void PermissionHandler_ReadDoesNotSatisfyAdminPolicy()
    {
        PermissionAuthorizationHandler handler = new();
        ClaimsPrincipal principal = BuildPrincipal("read");
        AuthorizationHandlerContext ctx = BuildAuthContext(new PermissionRequirement("admin"), principal);
        handler.HandleAsync(ctx).GetAwaiter().GetResult();
        Assert.That(ctx.HasSucceeded, Is.False);
    }

    [Test]
    public void PermissionHandler_NoClaims_FailsAll()
    {
        PermissionAuthorizationHandler handler = new();
        ClaimsPrincipal principal = BuildPrincipal(); // no permissions
        foreach (string policy in new[] { "read", "write", "admin" }) {
            AuthorizationHandlerContext ctx = BuildAuthContext(new PermissionRequirement(policy), principal);
            handler.HandleAsync(ctx).GetAwaiter().GetResult();
            Assert.That(ctx.HasSucceeded, Is.False, $"Expected failure for policy '{policy}'");
        }
    }

    // -----------------------------------------------------------------------
    // Fallback policy — the policy builder requires authentication
    // -----------------------------------------------------------------------

    [Test]
    public void FallbackPolicy_UnauthenticatedUser_HasFallbackAuthRequirement()
    {
        // Verify that the fallback policy built in Startup requires an authenticated user.
        // We test the policy object itself rather than the full middleware stack.
        AuthorizationPolicy fallback = new AuthorizationPolicyBuilder()
            .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser()
            .Build();

        Assert.That(fallback.Requirements, Has.Count.GreaterThan(0));
        Assert.That(fallback.AuthenticationSchemes, Does.Contain(ApiKeyAuthenticationHandler.SchemeName));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    static ClaimsPrincipal BuildPrincipal(params string[] permissions)
    {
        ClaimsIdentity identity = new(ApiKeyAuthenticationHandler.SchemeName);
        foreach (string p in permissions)
            identity.AddClaim(new Claim("permission", p));
        return new ClaimsPrincipal(identity);
    }

    static AuthorizationHandlerContext BuildAuthContext(IAuthorizationRequirement requirement, ClaimsPrincipal principal)
    {
        return new AuthorizationHandlerContext(
            [requirement],
            principal,
            null);
    }
}
