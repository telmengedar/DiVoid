using System;
using System.Collections.Generic;
using System.Linq;
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
using Microsoft.Extensions.DependencyInjection;
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
    // Fallback policy — Startup.ConfigureServices wires FallbackPolicy correctly
    // -----------------------------------------------------------------------

    [Test]
    public void FallbackPolicy_StartupConfigureServices_SetsFallbackPolicyOnAuthorizationOptions()
    {
        // Invoke the real Startup.ConfigureServices so that removing the FallbackPolicy
        // assignment from Startup causes this test to fail. Providing minimal configuration
        // with Auth:Enabled=true and an in-memory SQLite source so no real database file is
        // required during the service-registration phase.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Auth:Enabled"] = "true",
                ["DIVOID_KEY_PEPPER"] = TestPepper,
                ["Database:Type"] = "Sqlite",
                ["Database:Source"] = ":memory:"
            })
            .Build();

        IServiceCollection services = new ServiceCollection();
        // Startup expects IConfiguration to be resolvable from the container for some dependencies.
        services.AddSingleton(configuration);

        // Delegate all wiring to the real production code path.
        new Startup(configuration).ConfigureServices(services);

        using ServiceProvider provider = services.BuildServiceProvider();
        IOptions<AuthorizationOptions> authOptions = provider.GetRequiredService<IOptions<AuthorizationOptions>>();
        AuthorizationPolicy? fallback = authOptions.Value.FallbackPolicy;

        Assert.That(fallback, Is.Not.Null, "Startup must register a non-null FallbackPolicy");
        Assert.That(fallback.AuthenticationSchemes, Does.Contain(ApiKeyAuthenticationHandler.SchemeName),
            "FallbackPolicy must include the ApiKey authentication scheme");
        Assert.That(
            fallback.Requirements.OfType<Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement>().Any(),
            Is.True,
            "FallbackPolicy must require an authenticated user (DenyAnonymousAuthorizationRequirement)");
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
