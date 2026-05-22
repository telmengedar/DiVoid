#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// Regression tests that pin the empty-origins no-op branch in
/// <c>CorsExtensions.AddDivoidCors</c>.
///
/// When <c>Cors:AllowedOrigins</c> is absent (or empty), the extension calls
/// <c>policy.SetIsOriginAllowed(_ =&gt; false)</c> and returns early — CORS is
/// effectively disabled and no <c>Access-Control-Allow-Origin</c> header should
/// appear on any response.  This fixture spins its own
/// <see cref="WebApplicationFactory{TEntryPoint}"/> without any allowed-origins
/// config so it cannot share the two-origins factory in <see cref="CorsTests"/>.
///
/// Load-bearing property (DiVoid #275 / task #233): if the empty-origins guard
/// (<c>if (allowedOrigins.Length == 0) { policy.SetIsOriginAllowed(_ =&gt; false); return; }</c>)
/// is removed, a future change that falls through to <c>AllowAnyOrigin()</c> or an
/// open default would cause both tests below to fail.  The substitution probe in the
/// PR confirms whether the guard is actually load-bearing in the current ASP.NET Core
/// CORS implementation.
///
/// PR #34 (CORS policy), PR #105 (sibling auth-pipeline order test).
/// </summary>
[TestFixture]
public class CorsEmptyOriginsTests {

    WebApplicationFactory<Program> factory = null!;

    [OneTimeSetUp]
    public void Setup() {
        string dbPath = $"/tmp/divoid_cors_empty_origins_test_{Guid.NewGuid():N}.db3";

        // Clear ALL default config sources (including appsettings.json which ships with
        // two canonical origins) and supply only the minimal keys needed for the test host
        // to start.  Cors:AllowedOrigins is intentionally absent so the empty-origins guard
        // in AddDivoidCors fires: policy.SetIsOriginAllowed(_ => false); return;
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => {
            builder.ConfigureAppConfiguration((_, config) => {
                config.Sources.Clear();
                config.AddInMemoryCollection(new Dictionary<string, string?> {
                    ["Auth:Enabled"]    = "false",
                    ["Database:Type"]   = "Sqlite",
                    ["Database:Source"] = dbPath
                    // Cors:AllowedOrigins absent → empty array → no-op guard fires
                });
            });
        });
    }

    [OneTimeTearDown]
    public void TearDown() {
        factory.Dispose();
    }


    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    HttpClient CreateClient() => factory.CreateClient(new WebApplicationFactoryClientOptions {
        AllowAutoRedirect = false,
        HandleCookies = false
    });

    static string? AcaoHeader(HttpResponseMessage response) {
        return response.Headers.TryGetValues("Access-Control-Allow-Origin", out IEnumerable<string>? values)
            ? string.Join(", ", values)
            : null;
    }


    // -----------------------------------------------------------------------
    // Load-bearing: when no origins are configured, CORS must be a no-op.
    //
    // Both tests must fail if the empty-origins guard is removed and the
    // downstream WithOrigins()/AllowAnyOrigin() behaviour changes.
    // -----------------------------------------------------------------------

    /// <summary>
    /// OPTIONS preflight from any origin must receive no
    /// <c>Access-Control-Allow-Origin</c> header when no origins are configured.
    /// </summary>
    [Test]
    public async Task Preflight_From_Origin_NoOrigins_Configured_OmitsAcaoHeader() {
        HttpClient client = CreateClient();

        HttpRequestMessage request = new(HttpMethod.Options, "/api/nodes");
        request.Headers.Add("Origin", "http://localhost:3000");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        HttpResponseMessage response = await client.SendAsync(request);

        string? acao = AcaoHeader(response);
        Assert.That(acao, Is.Null.Or.Empty,
            "OPTIONS preflight with no origins configured must not produce Access-Control-Allow-Origin — " +
            "the empty-origins guard in AddDivoidCors must be active");
    }

    /// <summary>
    /// GET from any origin must receive no <c>Access-Control-Allow-Origin</c>
    /// header when no origins are configured.
    /// </summary>
    [Test]
    public async Task Get_From_Origin_NoOrigins_Configured_OmitsAcaoHeader() {
        HttpClient client = CreateClient();

        HttpRequestMessage request = new(HttpMethod.Get, "/api/nodes");
        request.Headers.Add("Origin", "http://localhost:3000");

        HttpResponseMessage response = await client.SendAsync(request);

        string? acao = AcaoHeader(response);
        Assert.That(acao, Is.Null.Or.Empty,
            "GET with no origins configured must not produce Access-Control-Allow-Origin — " +
            "the empty-origins guard in AddDivoidCors must be active");
    }
}
