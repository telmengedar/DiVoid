using Backend.tests.Fixtures;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Backend.tests.Tests;

/// <summary>Exercises the actual JWT metadata manager's configured transport.</summary>
[TestFixture, Parallelizable]
public class JwtMetadataTests {
    /// <summary>Checks both discovery and signing key retrieval through the fake backchannel.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Test, Parallelizable]
    public async Task GetConfigurationAsync_FixtureBackchannel_UsesCapturedDiscoveryAndKeys() {
        using JwtAuthFixture fixture = new();
        JwtBearerOptions options = fixture.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));

        OpenIdConnectConfiguration metadata = await options.ConfigurationManager!
            .GetConfigurationAsync(timeout.Token).WaitAsync(timeout.Token);

        Assert.That(metadata.Issuer, Is.EqualTo(JwtAuthFixture.TestAuthority));
        Assert.That(metadata.SigningKeys.Select(key => key.KeyId), Is.EqualTo(new[] { "test-key-1" }));
        Assert.That(fixture.DiscoveryRequests.Select(uri => uri.AbsoluteUri), Is.EquivalentTo(new[] {
            JwtAuthFixture.TestAuthority + "/.well-known/openid-configuration",
            JwtAuthFixture.TestAuthority + "/protocol/openid-connect/certs"
        }));
    }
}
