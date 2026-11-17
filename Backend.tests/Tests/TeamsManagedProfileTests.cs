using System.Net;
using Backend.tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Abstractions;
using Moq;

namespace Backend.tests.Tests;

/// <summary>Checks the shared Bot Framework credential authority independently of activity tenancy.</summary>
[TestFixture, Parallelizable]
public class TeamsManagedProfileTests {
    /// <summary>Checks actual named SDK credential options without an owner directory GUID.</summary>
    /// <param name="explicitTenant">Whether the fixed authority domain is explicitly configured.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task AddTeamsWebhook_ManagedCredentials_UsesFixedNativeOptions(bool explicitTenant) {
        Dictionary<string, string?> config = explicitTenant
            ? new() { ["AzureAd:TenantId"] = "botframework.com" }
            : new();
        using TeamsWebhookFixture fixture = new(overrides: config, graphBinding: true);
        MicrosoftIdentityApplicationOptions options = fixture.Services
            .GetRequiredService<IOptionsMonitor<MicrosoftIdentityApplicationOptions>>().Get("AzureAd");
        IConfiguration original = fixture.Services.GetRequiredService<IConfiguration>();

        using HttpResponseMessage response = await fixture.Post(fixture.Token());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(options.Instance, Is.EqualTo("https://login.microsoftonline.com/"));
        Assert.That(options.TenantId, Is.EqualTo("botframework.com"));
        Assert.That(options.Authority, Is.Empty);
        Assert.That(options.ClientId, Is.EqualTo(TeamsWebhookFixture.ClientId));
        Assert.That(options.ClientCredentials!.Count(), Is.EqualTo(1));
        Assert.That(options.ClientCredentials.Single().SourceType, Is.EqualTo(CredentialSource.ClientSecret));
        Assert.That(options.ClientCredentials.Single().ClientSecret == original["AzureAd:ClientCredentials:0:ClientSecret"], Is.True);
        Assert.That(original["AzureAd:TenantId"], explicitTenant ? Is.EqualTo("botframework.com") : Is.Null);
        Assert.That(fixture.Connector.Bodies, Is.Empty);
    }

    /// <summary>Checks the real SDK credential handler's scope and named authority selection.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Test, Parallelizable]
    public async Task ProcessAsync_NativeCredentialHandler_UsesSharedOptionsAndConnectorScope() {
        string? observedScope = null;
        string? observedName = null;
        Mock<IAuthorizationHeaderProvider> provider = new(MockBehavior.Strict);
        provider.Setup(auth => auth.CreateAuthorizationHeaderForAppAsync(It.IsAny<string>(),
                It.IsAny<AuthorizationHeaderProviderOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, AuthorizationHeaderProviderOptions, CancellationToken>((scope, options, _) => {
                observedScope = scope;
                observedName = options.AcquireTokenOptions.AuthenticationOptionsName;
            }).ReturnsAsync("Bearer synthetic-sdk-credential");
        using TeamsWebhookFixture fixture = new(graphBinding: true, preserveOutboundAuthentication: true, configureServices: services => {
            services.RemoveAll<IAuthorizationHeaderProvider>();
            services.AddSingleton(provider.Object);
        });

        using HttpResponseMessage response = await fixture.Post(fixture.Token());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await fixture.ReplyToQueuedMessage();
        provider.Verify(auth => auth.CreateAuthorizationHeaderForAppAsync(It.IsAny<string>(),
            It.IsAny<AuthorizationHeaderProviderOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.That(observedScope, Is.EqualTo("https://api.botframework.com/.default"));
        Assert.That(observedName, Is.EqualTo("AzureAd"));
        MicrosoftIdentityApplicationOptions effective = fixture.Services
            .GetRequiredService<IOptionsMonitor<MicrosoftIdentityApplicationOptions>>().Get(observedName!);
        Assert.That(effective.Instance, Is.EqualTo("https://login.microsoftonline.com/"));
        Assert.That(effective.TenantId, Is.EqualTo("botframework.com"));
        Assert.That(effective.Authority, Is.Empty);
        Assert.That(fixture.Connector.HasBearerAuthorization, Is.True);
        Assert.That(fixture.Connector.Bodies, Has.Count.EqualTo(1));
        Assert.That(fixture.Database.Invocations, Is.Empty);
    }

    /// <summary>Checks that valid conversation tenants do not select the credential authority.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Test, Parallelizable]
    public async Task ProcessAsync_TwoActivityTenants_UseSameSharedAuthority() {
        using TeamsWebhookFixture fixture = new(graphBinding: true);
        using HttpResponseMessage first = await fixture.Post(fixture.Token());
        Dictionary<string, object?> second = ActivityFor(Guid.NewGuid().ToString());

        using HttpResponseMessage next = await fixture.Post(fixture.Token(), second);

        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(next.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(fixture.Connector.Bodies, Is.Empty);
        MicrosoftIdentityApplicationOptions options = fixture.Services
            .GetRequiredService<IOptionsMonitor<MicrosoftIdentityApplicationOptions>>().Get("AzureAd");
        Assert.That(options.TenantId, Is.EqualTo("botframework.com"));
        Assert.That(fixture.Database.Invocations, Is.Empty);
    }

    /// <summary>Checks that the optional allowlist narrows only activity tenants.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Test, Parallelizable]
    public async Task ProcessAsync_ExplicitTenantAllowlist_RejectsOnlyUnlistedActivityTenant() {
        string secondTenant = Guid.NewGuid().ToString();
        using TeamsWebhookFixture fixture = new(graphBinding: true, overrides: new() {
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
            .Get("AzureAd").TenantId, Is.EqualTo("botframework.com"));
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
    [TestCase("AzureAd:TenantId", TeamsWebhookFixture.TenantId)]
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
