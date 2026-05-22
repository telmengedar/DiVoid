#nullable enable
using System;
using System.Collections.Generic;
using Backend.Services.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// Regression tests for fail-closed startup guards in <c>Startup.ConfigureServices</c>.
///
/// Load-bearing property (DiVoid #275): if the <see cref="MissingAudienceException"/> throw
/// in <c>Startup.ConfigureServices</c> is removed, <see cref="Startup_With_AuthEnabled_And_NoAudience_Throws_MissingAudienceException"/>
/// must fail — the host starts successfully and the <c>Assert.Throws</c> receives no exception.
///
/// Pinned here following Jenny's non-blocking finding from PR #27; exception message refined
/// in PR #104 (DiVoid #213).
/// </summary>
[TestFixture]
public class AuthStartupGuardTests {

    // -----------------------------------------------------------------------
    // Fail-closed: Auth:Enabled=true + empty Keycloak:Audience must throw
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that the service refuses to start when <c>Auth:Enabled=true</c> and
    /// <c>Keycloak:Audience</c> is not configured.
    ///
    /// The guard exists so that a misconfigured service cannot silently accept tokens
    /// without validating their audience claim — an open door to cross-client token replay.
    /// </summary>
    [Test]
    public void Startup_With_AuthEnabled_And_NoAudience_Throws_MissingAudienceException() {
        string dbPath = $"/tmp/divoid_audience_guard_test_{Guid.NewGuid():N}.db3";

        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => {
                builder.ConfigureAppConfiguration((_, config) => {
                    config.AddInMemoryCollection(new Dictionary<string, string?> {
                        ["Auth:Enabled"]    = "true",
                        ["Keycloak:Audience"] = "",          // intentionally empty
                        ["Database:Type"]   = "Sqlite",
                        ["Database:Source"] = dbPath,
                        ["DIVOID_KEY_PEPPER"] = "guard-test-pepper-32-bytes-minimum-0000"
                    });
                });
            });

        // Accessing factory.Server forces the host to build and ConfigureServices to run.
        // The throw must propagate out as MissingAudienceException (or wrap it in an inner
        // exception if the host bootstrap layer wraps it).
        Assert.That(
            () => { _ = factory.Server; },
            Throws.TypeOf<MissingAudienceException>()
                  .Or.InnerException.TypeOf<MissingAudienceException>(),
            "Service must refuse to start with Auth:Enabled=true and empty Keycloak:Audience");
    }
}
