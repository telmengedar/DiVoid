using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// Integration tests that assert CORS headers are present for allowed origins and absent
/// for unlisted origins.
///
/// Tests use <see cref="WebApplicationFactory{TEntryPoint}"/> with auth disabled to keep
/// focus on CORS behaviour; auth correctness is covered by <see cref="AuthErrorBodyTests"/>.
///
/// Note on WebApplicationFactory and CORS: the test server's internal loopback transport
/// bypasses the real TCP stack. ASP.NET Core's CORS middleware still evaluates the
/// <c>Origin</c> request header and writes (or withholds) the CORS response headers — the
/// only thing the test server does not simulate is the browser's actual same-origin policy
/// enforcement, which is orthogonal to what we are testing here.
/// </summary>
[TestFixture]
public class CorsTests {

    WebApplicationFactory<Program> factory = null!;

    [OneTimeSetUp]
    public void Setup() {
        string dbName = $"/tmp/divoid_cors_test_{Guid.NewGuid():N}.db3";
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => {
            builder.ConfigureAppConfiguration((_, config) => {
                config.AddInMemoryCollection(new Dictionary<string, string?> {
                    ["Auth:Enabled"]            = "false",
                    ["Database:Type"]           = "Sqlite",
                    ["Database:Source"]         = dbName,
                    // Inject the two canonical allowed origins
                    ["Cors:AllowedOrigins:0"]   = "http://localhost:3000",
                    ["Cors:AllowedOrigins:1"]   = "https://divoid.mamgo.io"
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

    static string? AcamHeader(HttpResponseMessage response) {
        return response.Headers.TryGetValues("Access-Control-Allow-Methods", out IEnumerable<string>? values)
            ? string.Join(", ", values)
            : null;
    }


    // -----------------------------------------------------------------------
    // Preflight (OPTIONS) from an allowed origin → 204 with CORS headers
    // -----------------------------------------------------------------------

    [Test]
    public async Task Preflight_AllowedOrigin_LocalhostDev_Returns204WithCorsHeaders() {
        HttpClient client = CreateClient();
        HttpRequestMessage request = new(HttpMethod.Options, "/api/nodes");
        request.Headers.Add("Origin", "http://localhost:3000");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "Authorization");

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.That((int)response.StatusCode, Is.EqualTo(204),
            "preflight from an allowed origin must return 204");

        string? acao = AcaoHeader(response);
        Assert.That(acao, Is.EqualTo("http://localhost:3000"),
            "Access-Control-Allow-Origin must reflect the request origin for allowed origins");

        string? acam = AcamHeader(response);
        Assert.That(acam, Is.Not.Null.And.Not.Empty,
            "Access-Control-Allow-Methods must be present on preflight response");
    }

    [Test]
    public async Task Preflight_AllowedOrigin_ProdDomain_Returns204WithCorsHeaders() {
        HttpClient client = CreateClient();
        HttpRequestMessage request = new(HttpMethod.Options, "/api/nodes");
        request.Headers.Add("Origin", "https://divoid.mamgo.io");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "Authorization");

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.That((int)response.StatusCode, Is.EqualTo(204),
            "preflight from prod origin must return 204");

        string? acao = AcaoHeader(response);
        Assert.That(acao, Is.EqualTo("https://divoid.mamgo.io"),
            "Access-Control-Allow-Origin must reflect the prod origin");
    }


    // -----------------------------------------------------------------------
    // Actual cross-origin GET from an allowed origin → ACAO header present
    // -----------------------------------------------------------------------

    [Test]
    public async Task Get_AllowedOrigin_ResponseContainsAcaoHeader() {
        HttpClient client = CreateClient();
        HttpRequestMessage request = new(HttpMethod.Get, "/api/nodes");
        request.Headers.Add("Origin", "http://localhost:3000");

        HttpResponseMessage response = await client.SendAsync(request);

        // Auth is disabled in test factory, so this should be 200
        Assert.That((int)response.StatusCode, Is.EqualTo(200),
            "GET /api/nodes with auth disabled must return 200");

        string? acao = AcaoHeader(response);
        Assert.That(acao, Is.EqualTo("http://localhost:3000"),
            "GET response from an allowed origin must include Access-Control-Allow-Origin");
    }


    // -----------------------------------------------------------------------
    // Request from an unlisted origin → no CORS headers in response
    // -----------------------------------------------------------------------

    [Test]
    public async Task Preflight_UnlistedOrigin_NoCorsHeadersInResponse() {
        HttpClient client = CreateClient();
        HttpRequestMessage request = new(HttpMethod.Options, "/api/nodes");
        request.Headers.Add("Origin", "https://evil.example.com");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "Authorization");

        HttpResponseMessage response = await client.SendAsync(request);

        string? acao = AcaoHeader(response);
        Assert.That(acao, Is.Null.Or.Empty,
            "response to an unlisted origin must not include Access-Control-Allow-Origin");
    }

    [Test]
    public async Task Get_UnlistedOrigin_NoCorsHeaderInResponse() {
        HttpClient client = CreateClient();
        HttpRequestMessage request = new(HttpMethod.Get, "/api/nodes");
        request.Headers.Add("Origin", "https://evil.example.com");

        HttpResponseMessage response = await client.SendAsync(request);

        string? acao = AcaoHeader(response);
        Assert.That(acao, Is.Null.Or.Empty,
            "GET response for an unlisted origin must not include Access-Control-Allow-Origin");
    }
}
