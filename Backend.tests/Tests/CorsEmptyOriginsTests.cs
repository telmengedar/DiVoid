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
/// Regression tests that pin the empty-origins no-op behaviour in
/// <c>CorsExtensions.AddDivoidCors</c>.
///
/// When <c>Cors:AllowedOrigins</c> is absent (or empty),
/// <c>policy.WithOrigins(new string[0])</c> leaves the ASP.NET Core
/// <c>CorsPolicy.Origins</c> collection empty.  The default
/// <c>IsOriginAllowed</c> delegate performs <c>Origins.Contains(origin)</c>
/// which returns <c>false</c> for every origin — so no
/// <c>Access-Control-Allow-Origin</c> header is emitted.  No explicit guard
/// predicate is needed; the behaviour is a natural consequence of an empty
/// origins list (confirmed via ASP.NET Core source:
/// <c>CorsPolicyBuilder.WithOrigins</c> / <c>CorsPolicy.DefaultIsOriginAllowed</c>,
/// dotnet/aspnetcore v9.0.5).
///
/// This fixture spins its own <see cref="WebApplicationFactory{TEntryPoint}"/>
/// without any allowed-origins config so it cannot share the two-origins
/// factory in <see cref="CorsTests"/>.
///
/// Load-bearing property (DiVoid #275 / task #233 / task #877): if a future
/// change introduces <c>AllowAnyOrigin()</c> or any other open default in the
/// empty-origins branch, both tests below will fail — that is the intended
/// trip-wire.
///
/// PR #34 (CORS policy), PR #109 (substitution-probe regression), PR #112 (guard drop).
/// </summary>
[TestFixture]
public class CorsEmptyOriginsTests {

    WebApplicationFactory<Program> factory = null!;

    [OneTimeSetUp]
    public void Setup() {
        string dbPath = $"/tmp/divoid_cors_empty_origins_test_{Guid.NewGuid():N}.db3";

        // Clear ALL default config sources (including appsettings.json which ships with
        // two canonical origins) and supply only the minimal keys needed for the test host
        // to start.  Cors:AllowedOrigins is intentionally absent so WithOrigins receives
        // an empty array — ASP.NET Core's default IsOriginAllowed then returns false for
        // every origin, producing no ACAO header.
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => {
            builder.ConfigureAppConfiguration((_, config) => {
                config.Sources.Clear();
                config.AddInMemoryCollection(new Dictionary<string, string?> {
                    ["Auth:Enabled"]    = "false",
                    ["Database:Type"]   = "Sqlite",
                    ["Database:Source"] = dbPath
                    // Cors:AllowedOrigins absent → empty array → WithOrigins([]) → no ACAO
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
    // Both tests trip if a future change introduces AllowAnyOrigin() or any
    // other open default in the empty-origins branch of AddDivoidCors.
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
            "WithOrigins([]) must not emit ACAO headers (ASP.NET Core default IsOriginAllowed returns false)");
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
            "WithOrigins([]) must not emit ACAO headers (ASP.NET Core default IsOriginAllowed returns false)");
    }
}
