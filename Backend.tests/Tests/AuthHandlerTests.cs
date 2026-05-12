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
using Backend.Models.Users;
using Backend.Services.Auth;
using Backend.tests.Fixtures;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.AspNetCore.Services.Patches;

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
        return await fixture.EntityManager.Insert<User>()
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
                ["Auth:Enabled"]       = "true",
                ["DIVOID_KEY_PEPPER"]  = TestPepper,
                ["Database:Type"]      = "Sqlite",
                ["Database:Source"]    = ":memory:",
                // Keycloak:Audience must be non-empty when Auth:Enabled=true
                // (startup fails closed if it is empty — tested separately)
                ["Keycloak:Audience"]  = "test-audience-value"
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
        // Under PolicyScheme, policies do NOT carry explicit per-scheme lists.
        // The DiVoidBearer PolicyScheme is the default authenticate scheme and dispatches
        // to JwtBearer or ApiKey based on token shape. Per-policy scheme lists caused the
        // framework to call ForbidAsync on every listed scheme, producing spurious log noise.
        Assert.That(fallback.AuthenticationSchemes, Is.Empty,
            "FallbackPolicy must NOT enumerate individual schemes — PolicyScheme dispatches scheme selection");
        Assert.That(
            fallback.Requirements.OfType<Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement>().Any(),
            Is.True,
            "FallbackPolicy must require an authenticated user (DenyAnonymousAuthorizationRequirement)");
    }

    // -----------------------------------------------------------------------
    // Named policies — Startup.ConfigureServices does NOT enumerate schemes
    // -----------------------------------------------------------------------

    [Test]
    public void NamedPolicies_DoNotEnumerateAuthenticationSchemes()
    {
        // Pins the contract: under PolicyScheme dispatch, named policies must not carry
        // per-policy authentication scheme lists. If they did, the framework would invoke
        // ForbidAsync on each listed scheme, emitting spurious log noise on every 403.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Auth:Enabled"]      = "true",
                ["DIVOID_KEY_PEPPER"] = TestPepper,
                ["Database:Type"]     = "Sqlite",
                ["Database:Source"]   = ":memory:",
                ["Keycloak:Audience"] = "test-audience-value"
            })
            .Build();

        IServiceCollection services = new ServiceCollection();
        services.AddSingleton(configuration);
        new Startup(configuration).ConfigureServices(services);

        using ServiceProvider provider = services.BuildServiceProvider();
        IOptions<AuthorizationOptions> authOptions = provider.GetRequiredService<IOptions<AuthorizationOptions>>();

        foreach (string policyName in new[] { "admin", "write", "read" }) {
            AuthorizationPolicy? policy = authOptions.Value.GetPolicy(policyName);
            Assert.That(policy, Is.Not.Null, $"Policy '{policyName}' must be registered");
            Assert.That(policy!.AuthenticationSchemes, Is.Empty,
                $"Policy '{policyName}' must not enumerate authentication schemes — PolicyScheme handles dispatch");
        }
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

    // -----------------------------------------------------------------------
    // JWT-shape guard — ApiKeyAuthenticationHandler returns NoResult for JWTs
    // -----------------------------------------------------------------------

    /// <summary>
    /// ApiKeyAuthenticationHandler must return NoResult immediately when the bearer
    /// value has exactly 2 dots (compact JWT serialization: header.payload.signature).
    /// The database must never be consulted for JWT-shaped tokens — falling through to
    /// a DB lookup and failing it would log a misleading "invalid api key" error when
    /// the real issue is on the JWT path (expired, wrong audience, bad signature, etc.).
    /// </summary>
    [Test]
    public async Task ApiKeyHandler_JwtShapedBearer_ReturnsNoResult_WithoutCallingDatabase()
    {
        // Arrange: stub service that throws if GetApiKey is ever called.
        // If the guard fires correctly, this will never be reached.
        ThrowIfCalledApiKeyService stub = new();

        IOptionsMonitor<AuthenticationSchemeOptions> options =
            new TestOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions());

        ApiKeyAuthenticationHandler handler = new(
            options,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            stub);

        // Set up an HttpContext with a bearer value that looks like a JWT (exactly 2 dots).
        DefaultHttpContext httpContext = new();
        httpContext.Request.Headers["Authorization"] = "Bearer eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ0ZXN0In0.fake-signature";

        AuthenticationScheme scheme = new(
            ApiKeyAuthenticationHandler.SchemeName,
            displayName: null,
            typeof(ApiKeyAuthenticationHandler));

        await handler.InitializeAsync(scheme, httpContext);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert: must abstain (NoResult) and must not have called the database
        Assert.That(result.None, Is.True,
            "ApiKeyAuthenticationHandler must return NoResult for a JWT-shaped bearer (2 dots)");
        Assert.That(stub.WasCalled, Is.False,
            "GetApiKey must not be called for a JWT-shaped bearer — the guard must short-circuit before the DB");
    }
}

// ---------------------------------------------------------------------------
// Test doubles
// ---------------------------------------------------------------------------

/// <summary>
/// IApiKeyService stub that records whether GetApiKey was ever called and throws
/// if it is, so that tests can assert the JWT-shape guard fires before any DB access.
/// </summary>
file sealed class ThrowIfCalledApiKeyService : IApiKeyService
{
    public bool WasCalled { get; private set; }

    public Task<ApiKeyDetails> GetApiKey(string fullKey)
    {
        WasCalled = true;
        throw new InvalidOperationException("GetApiKey must not be called for a JWT-shaped bearer token");
    }

    public Task<ApiKeyDetails> CreateApiKey(ApiKeyParameters apiKey) =>
        throw new NotSupportedException();

    public Task<ApiKeyDetails> GetApiKeyById(long keyId) =>
        throw new NotSupportedException();

    public AsyncPageResponseWriter<ApiKeyDetails> ListApiKeys(ListFilter filter = null!) =>
        throw new NotSupportedException();

    public Task<ApiKeyDetails> UpdateApiKey(long keyId, params PatchOperation[] patches) =>
        throw new NotSupportedException();

    public Task DeleteApiKey(long keyId) =>
        throw new NotSupportedException();

    public Task<bool> AnyAdminKeyExists() =>
        throw new NotSupportedException();
}

/// <summary>
/// Minimal IOptionsMonitor implementation for tests that always returns the same value.
/// </summary>
file sealed class TestOptionsMonitor<TOptions> : IOptionsMonitor<TOptions>
{
    readonly TOptions value;

    public TestOptionsMonitor(TOptions value) => this.value = value;

    public TOptions CurrentValue => value;

    public TOptions Get(string? name) => value;

    public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
}
