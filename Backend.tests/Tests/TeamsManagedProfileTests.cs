using System.Net;
using Backend.tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Http;
using Microsoft.Identity.Abstractions;
using Moq;

namespace Backend.tests.Tests;

/// <summary>Checks the selected Bot Framework credential authority independently of activity tenancy.</summary>
[TestFixture, Parallelizable]
public class TeamsManagedProfileTests {
    const string HostingTenant = "8165ad9e-f318-4192-b1bf-3ab83959819f";

    /// <summary>Checks actual named SDK credential options for host and legacy profiles.</summary>
    /// <param name="tenant">The optional configured credential tenant.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(null), TestCase("botframework.com"), TestCase(HostingTenant)]
    [TestCase("8165AD9E-F318-4192-B1BF-3AB83959819F"), Parallelizable]
    public async Task AddTeamsWebhook_ManagedCredentials_UsesSelectedNativeOptions(string? tenant) {
        Dictionary<string, string?> config = new() { ["AzureAd:TenantId"] = tenant };
        string expected = tenant == null || tenant == "botframework.com" ? "botframework.com" : HostingTenant;
        using TeamsWebhookFixture fixture = new(overrides: config, graphBinding: true, environmentName: Microsoft.Extensions.Hosting.Environments.Production);
        MicrosoftIdentityApplicationOptions options = fixture.Services
            .GetRequiredService<IOptionsMonitor<MicrosoftIdentityApplicationOptions>>().Get("AzureAd");
        IConfiguration original = fixture.Services.GetRequiredService<IConfiguration>();

        using HttpResponseMessage response = await fixture.Post(fixture.Token());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(fixture.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>().EnvironmentName, Is.EqualTo("Production"));
        Assert.That(original.GetValue<bool>("Auth:Enabled"), Is.True);
        Assert.That(options.Instance, Is.EqualTo("https://login.microsoftonline.com/"));
        Assert.That(options.TenantId, Is.EqualTo(expected));
        Assert.That(fixture.Services.GetRequiredService<IOptionsMonitor<Microsoft.Identity.Web.MicrosoftIdentityOptions>>()
            .Get("AzureAd").TenantId, Is.EqualTo(expected));
        Assert.That(options.Authority, Is.Empty);
        Assert.That(options.ClientId, Is.EqualTo(TeamsWebhookFixture.ClientId));
        Assert.That(options.ClientCredentials!.Count(), Is.EqualTo(1));
        Assert.That(options.ClientCredentials!.Single().SourceType, Is.EqualTo(CredentialSource.ClientSecret));
        Assert.That(options.ClientCredentials!.Single().ClientSecret == original["AzureAd:ClientCredentials:0:ClientSecret"], Is.True);
        Assert.That(original["AzureAd:TenantId"], Is.EqualTo(tenant));
        Assert.That(fixture.Connector.Bodies, Is.Empty);
    }

    /// <summary>Checks the real SDK credential handler's scope and named authority selection.</summary>
    /// <param name="diagnostic">Whether the separately bounded diagnostic client issues the request.</param>
    /// <param name="tenant">The configured credential tenant.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(false, "botframework.com"), TestCase(true, "botframework.com")]
    [TestCase(false, HostingTenant), TestCase(true, HostingTenant), Parallelizable]
    public async Task ProcessAsync_NativeCredentialHandler_UsesSelectedOptionsAndConnectorScope(bool diagnostic, string tenant) {
        string? observedScope = null;
        string? observedName = null;
        Mock<IAuthorizationHeaderProvider> provider = new(MockBehavior.Strict);
        provider.Setup(auth => auth.CreateAuthorizationHeaderForAppAsync(It.IsAny<string>(),
                It.IsAny<AuthorizationHeaderProviderOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, AuthorizationHeaderProviderOptions, CancellationToken>((scope, options, _) => {
                observedScope = scope;
                observedName = options.AcquireTokenOptions.AuthenticationOptionsName;
            }).ReturnsAsync("Bearer synthetic-sdk-credential");
        using TeamsWebhookFixture fixture = new(overrides: new() { ["AzureAd:TenantId"] = tenant },
            graphBinding: true, preserveOutboundAuthentication: true, configureServices: services => {
            services.RemoveAll<IAuthorizationHeaderProvider>();
            services.AddSingleton(provider.Object);
        });

        using HttpResponseMessage response = await fixture.Post(fixture.Token());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        if (diagnostic) {
            fixture.Connector.ResponseBody = "[]";
            using HttpClient http = fixture.Services.GetRequiredService<IHttpClientFactory>().CreateClient("TeamsConnectorDiagnosis");
            Assert.That(http.Timeout, Is.EqualTo(TimeSpan.FromSeconds(15)));
            await new Microsoft.Teams.Core.ConversationClient(http).GetConversationMembersAsync("synthetic-conversation", new Uri(TeamsWebhookFixture.ServiceUrl));
            Assert.That(fixture.DiagnosisRedirectsDisabled, Is.True);
        } else {
            await fixture.ReplyToQueuedMessage();
            Assert.That(fixture.ReplyRedirectsEnabled, Is.True);
            using HttpClient http = fixture.Services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(Microsoft.Teams.Core.ConversationClient));
            Assert.That(http.Timeout, Is.EqualTo(TimeSpan.FromSeconds(100)));
        }
        provider.Verify(auth => auth.CreateAuthorizationHeaderForAppAsync(It.IsAny<string>(),
            It.IsAny<AuthorizationHeaderProviderOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.That(observedScope, Is.EqualTo("https://api.botframework.com/.default"));
        Assert.That(observedName, Is.EqualTo("AzureAd"));
        MicrosoftIdentityApplicationOptions effective = fixture.Services
            .GetRequiredService<IOptionsMonitor<MicrosoftIdentityApplicationOptions>>().Get(observedName!);
        Assert.That(effective.Instance, Is.EqualTo("https://login.microsoftonline.com/"));
        Assert.That(effective.TenantId, Is.EqualTo(tenant));
        Assert.That(effective.Authority, Is.Empty);
        Assert.That(fixture.Connector.HasBearerAuthorization, Is.True);
        Assert.That(fixture.Connector.Bodies, Has.Count.EqualTo(diagnostic ? 0 : 1));
        Assert.That(fixture.Connector.Methods.Single(), Is.EqualTo(diagnostic ? HttpMethod.Get : HttpMethod.Post));
        Assert.That(fixture.Database.Invocations, Is.Empty);
    }

    /// <summary>Checks real native token-provider selection reaches the approved authority without external traffic.</summary>
    /// <param name="nativeClient">Whether to traverse the native conversation client authentication handler.</param>
    /// <param name="tenant">The configured credential tenant.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(false, "botframework.com"), TestCase(true, "botframework.com")]
    [TestCase(false, HostingTenant), TestCase(true, HostingTenant), Parallelizable]
    public async Task NativeAuthorizationProvider_RegisteredAzureAd_ReachesAuthorityBoundary(bool nativeClient, string tenant) {
        NativeAuthorityBoundary boundary = new(tenant);
        using TeamsWebhookFixture fixture = new(overrides: new() { ["AzureAd:TenantId"] = tenant },
            preserveOutboundAuthentication: true, environmentName: Microsoft.Extensions.Hosting.Environments.Production, configureServices: services => {
            services.ConfigureAll<HttpClientFactoryOptions>(options => options.HttpMessageHandlerBuilderActions.Add(builder => {
                builder.PrimaryHandler = boundary;
            }));
        });
        Microsoft.AspNetCore.Http.DefaultHttpContext context = new();
        context.Request.Scheme = "https";
        context.Request.Host = new Microsoft.AspNetCore.Http.HostString("in-process.invalid");
        context.RequestServices = fixture.Services;
        context.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            new[] { new System.Security.Claims.Claim("divoid.user_id", "17"), new System.Security.Claims.Claim("permission", "write") }, "ApiKey"));
        fixture.Services.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext = context;
        IAuthorizationHeaderProvider provider = fixture.Services.GetRequiredService<IAuthorizationHeaderProvider>();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Exception? failure = Assert.CatchAsync<Exception>(async () => {
            if (nativeClient)
                await fixture.Services.GetRequiredService<Microsoft.Teams.Core.ConversationClient>().SendActivityAsync(
                    "in-process-only", new Microsoft.Teams.Apps.MessageActivityInput { Text = "not sent" },
                    new Uri(TeamsWebhookFixture.ServiceUrl), requestContext: new Microsoft.Teams.Core.Http.BotRequestContext { BotAppId = TeamsWebhookFixture.ClientId },
                    cancellationToken: timeout.Token);
            else
                await provider.CreateAuthorizationHeaderForAppAsync("https://api.botframework.com/.default", new AuthorizationHeaderProviderOptions {
                    AcquireTokenOptions = new AcquireTokenOptions { AuthenticationOptionsName = "AzureAd" }
                }, timeout.Token);
        });
        Assert.That(boundary.Observed, Is.Not.Empty);
        Assert.That(boundary.Observed.All(allowed => allowed), Is.True);
        Assert.That(boundary.TokenRequests, Is.EqualTo(1));
        Assert.That(boundary.TokenScopeMatches && boundary.CredentialMatches, Is.True);
        Assert.That(failure, Is.Not.Null);
        Assert.That(ExceptionChain(failure!).Any(exception => exception.Message.Contains("IDW10503", StringComparison.Ordinal)), Is.False);
        Assert.That(fixture.Connector.Bodies, Is.Empty);
        Assert.That(fixture.Database.Invocations, Is.Empty);
    }

    /// <summary>Checks that valid conversation tenants do not select the credential authority.</summary>
    /// <param name="tenant">The configured credential tenant.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase("botframework.com"), TestCase(HostingTenant), Parallelizable]
    public async Task ProcessAsync_TwoActivityTenants_UseSelectedAuthority(string tenant) {
        using TeamsWebhookFixture fixture = new(graphBinding: true, overrides: new() { ["AzureAd:TenantId"] = tenant });
        using HttpResponseMessage first = await fixture.Post(fixture.Token());
        Dictionary<string, object?> second = ActivityFor(Guid.NewGuid().ToString());

        using HttpResponseMessage next = await fixture.Post(fixture.Token(), second);

        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(next.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(fixture.Connector.Bodies, Is.Empty);
        MicrosoftIdentityApplicationOptions options = fixture.Services
            .GetRequiredService<IOptionsMonitor<MicrosoftIdentityApplicationOptions>>().Get("AzureAd");
        Assert.That(options.TenantId, Is.EqualTo(tenant));
        Assert.That(fixture.Database.Invocations, Is.Empty);
    }

    /// <summary>Checks that the optional allowlist narrows only activity tenants.</summary>
    /// <param name="tenant">The configured credential tenant.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase("botframework.com"), TestCase(HostingTenant), Parallelizable]
    public async Task ProcessAsync_ExplicitTenantAllowlist_RejectsOnlyUnlistedActivityTenant(string tenant) {
        string secondTenant = Guid.NewGuid().ToString();
        using TeamsWebhookFixture fixture = new(graphBinding: true, overrides: new() {
            ["AzureAd:TenantId"] = tenant,
            ["Teams:AllowedTenantIds:0"] = TeamsWebhookFixture.TenantId,
            ["Teams:AllowedTenantIds:1"] = secondTenant
        });
        using HttpResponseMessage first = await fixture.Post(fixture.Token());
        using HttpResponseMessage second = await fixture.Post(fixture.Token(), ActivityFor(secondTenant));

        using HttpResponseMessage denied = await fixture.Post(fixture.Token(), ActivityFor(Guid.NewGuid().ToString()));

        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(fixture.Connector.Bodies, Is.Empty);
        Assert.That(fixture.Services.GetRequiredService<IOptionsMonitor<MicrosoftIdentityApplicationOptions>>()
            .Get("AzureAd").TenantId, Is.EqualTo(tenant));
    }

    /// <summary>Checks mandatory activity tenant syntax and conversation consistency.</summary>
    /// <param name="tenant">The malformed activity tenant.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(""), TestCase("botframework.com"), TestCase("00000000-0000-0000-0000-000000000000")]
    [Parallelizable]
    public async Task ProcessAsync_InvalidActivityTenant_RejectsBeforeSend(string tenant) {
        using TeamsWebhookFixture fixture = new();
        Dictionary<string, object?> activity = ActivityFor(tenant);

        using HttpResponseMessage response = await fixture.Post(fixture.Token(), activity);

        Assert.That(fixture.Metadata.KeyRequests, Is.GreaterThan(0));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(fixture.Connector.Bodies, Is.Empty);
        Assert.That(fixture.Database.Invocations, Is.Empty);
    }

    /// <summary>Checks fail-closed credential and activity-policy configuration.</summary>
    /// <param name="field">The invalid field.</param>
    /// <param name="value">The invalid configuration value.</param>
    [TestCase("AzureAd:TenantId", "common")]
    [TestCase("AzureAd:TenantId", "organizations")]
    [TestCase("AzureAd:TenantId", "")]
    [TestCase("AzureAd:TenantId", " ")]
    [TestCase("AzureAd:TenantId", "00000000-0000-0000-0000-000000000000")]
    [TestCase("AzureAd:TenantId", "8165ad9e-f318-4192-b1bf-3ab83959819z")]
    [TestCase("AzureAd:TenantId", "another.example")]
    [TestCase("AzureAd:Instance", "https://untrusted.invalid/")]
    [TestCase("AzureAd:Authority", "https://login.microsoftonline.com/common")]
    [TestCase("Teams:AllowedTenantIds:0", "")]
    [TestCase("Teams:AllowedTenantIds:0", "common")]
    [TestCase("Teams:AllowedTenantIds:0", "00000000-0000-0000-0000-000000000000")]
    [TestCase("Teams:AllowedTenantIds", TeamsWebhookFixture.TenantId)]
    [Parallelizable]
    public void AddTeamsWebhook_UnsafeProfileConfiguration_FailsStartup(string field, string value) {
        InvalidOperationException? failure = Assert.Throws<InvalidOperationException>(() => {
            using TeamsWebhookFixture fixture = new(overrides: new() { [field] = value });
        });

        Assert.That(failure!.Message, Does.Contain("Teams").Or.Contain("AzureAd"));
        Assert.That(failure.Message.Contains("synthetic-fixture-secret"), Is.False);
    }

    static IEnumerable<Exception> ExceptionChain(Exception failure) {
        for (Exception? current = failure; current != null; current = current.InnerException)
            yield return current;
    }

    static Dictionary<string, object?> ActivityFor(string tenant) {
        Dictionary<string, object?> activity = TeamsWebhookFixture.Activity();
        activity["channelData"] = new Dictionary<string, object> {
            ["tenant"] = new Dictionary<string, object> { ["id"] = tenant }
        };
        activity["conversation"] = new Dictionary<string, object> {
            ["id"] = "independent-conversation", ["tenantId"] = tenant
        };
        return activity;
    }
}
