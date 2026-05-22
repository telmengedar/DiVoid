#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Backend.tests.Fixtures;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// Regression test that pins the middleware ordering in <c>Startup.Configure</c>:
/// <c>UseDivoidCors()</c> must run <em>before</em> <c>UseAuthentication()</c>.
///
/// The five tests in <see cref="CorsTests"/> all run with <c>Auth:Enabled=false</c>,
/// which means a middleware re-ordering (e.g. moving <c>UseDivoidCors</c> after
/// <c>UseAuthentication</c>) would pass all five existing tests while silently breaking
/// real preflight behaviour in production. This single test fills that gap.
///
/// Load-bearing property (DiVoid #275): if <c>UseDivoidCors()</c> is moved after
/// <c>UseAuthentication()</c> in <c>Startup.Configure</c>, this test must fail with
/// a 401 because the <c>OPTIONS</c> preflight carries no <c>Authorization</c> header
/// and the authentication middleware rejects it before CORS headers are written.
/// </summary>
[TestFixture]
public class CorsPreflightAuthPipelineOrderTests {

    WebApplicationFactory<Program> factory = null!;

    [OneTimeSetUp]
    public void Setup() {
        string dbPath = $"/tmp/divoid_cors_auth_order_test_{Guid.NewGuid():N}.db3";

        // Build RSA signing key the same way JwtAuthFixture does so the JWT middleware
        // starts without a live Keycloak instance. CORS origins are layered on top.
        System.Security.Cryptography.RSA rsa = System.Security.Cryptography.RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "cors-order-test-key" };

        string jwks = System.Text.Json.JsonSerializer.Serialize(new {
            keys = new[] {
                new {
                    kty = "RSA",
                    use = "sig",
                    kid = signingKey.KeyId,
                    alg = "RS256",
                    n   = Base64UrlEncoder.Encode(rsa.ExportParameters(false).Modulus!),
                    e   = Base64UrlEncoder.Encode(rsa.ExportParameters(false).Exponent!)
                }
            }
        });
        string discoveryJson = System.Text.Json.JsonSerializer.Serialize(new {
            issuer                                  = JwtAuthFixture.TestAuthority,
            jwks_uri                                = $"{JwtAuthFixture.TestAuthority}/protocol/openid-connect/certs",
            authorization_endpoint                  = $"{JwtAuthFixture.TestAuthority}/protocol/openid-connect/auth",
            token_endpoint                          = $"{JwtAuthFixture.TestAuthority}/protocol/openid-connect/token",
            id_token_signing_alg_values_supported   = new[] { "RS256" }
        });

        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => {
            builder.ConfigureAppConfiguration((_, config) => {
                config.AddInMemoryCollection(new Dictionary<string, string?> {
                    ["Auth:Enabled"]                  = "true",
                    ["DIVOID_KEY_PEPPER"]             = JwtAuthFixture.TestPepper,
                    ["Database:Type"]                 = "Sqlite",
                    ["Database:Source"]               = dbPath,
                    ["Keycloak:Authority"]            = JwtAuthFixture.TestAuthority,
                    ["Keycloak:Audience"]             = JwtAuthFixture.TestAudience,
                    ["Keycloak:RequireHttpsMetadata"] = "false",
                    ["Keycloak:UserIdClaimName"]      = "userId",
                    // CORS origins — same as CorsTests to keep assertions symmetrical
                    ["Cors:AllowedOrigins:0"]         = "http://localhost:3000",
                    ["Cors:AllowedOrigins:1"]         = "https://divoid.mamgo.io"
                });
            });
            builder.ConfigureServices(services => {
                // Intercept OIDC metadata + JWKS discovery so no live Keycloak instance
                // is required — mirrors the pattern used by JwtAuthFixture exactly.
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options => {
                    options.BackchannelHttpHandler = new JwtAuthFixture.FakeJwksHandlerPublic(
                        JwtAuthFixture.TestAuthority, discoveryJson, jwks);
                    options.TokenValidationParameters.IssuerSigningKey  = signingKey;
                    options.TokenValidationParameters.IssuerSigningKeys = [signingKey];
                });
            });
        });
    }

    [OneTimeTearDown]
    public void TearDown() {
        factory.Dispose();
    }


    // -----------------------------------------------------------------------
    // Load-bearing: preflight from an allowed origin must succeed even when
    // the real UseAuthentication middleware is in the pipeline.
    //
    // This test FAILS if UseDivoidCors() is moved after UseAuthentication()
    // in Startup.Configure, because the OPTIONS request carries no Authorization
    // header and the auth middleware rejects it before CORS can respond.
    // The existing CorsTests all pass in that scenario because Auth:Enabled=false
    // means UseAuthentication() is never called.
    // -----------------------------------------------------------------------

    [Test]
    public async Task Preflight_FromAllowedOrigin_With_Auth_Enabled_Succeeds() {
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        HttpRequestMessage request = new(HttpMethod.Options, "/api/nodes");
        request.Headers.Add("Origin", "http://localhost:3000");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "Authorization");

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.That((int)response.StatusCode, Is.EqualTo(204),
            "OPTIONS preflight from an allowed origin must return 204 even with Auth:Enabled=true — " +
            "UseDivoidCors() must run before UseAuthentication() in Startup.Configure");

        string? acao = response.Headers.TryGetValues("Access-Control-Allow-Origin", out System.Collections.Generic.IEnumerable<string>? values)
            ? string.Join(", ", values)
            : null;

        Assert.That(acao, Is.EqualTo("http://localhost:3000"),
            "Access-Control-Allow-Origin must reflect the allowed origin in the preflight response");
    }
}
