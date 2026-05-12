#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Pooshit.Ocelot.Entities;

namespace Backend.tests.Fixtures;

/// <summary>
/// captures formatted log messages in memory for assertion in tests.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    readonly ConcurrentBag<string> messages = new();

    public IReadOnlyCollection<string> Messages => messages;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, messages);

    public void Dispose() { }

    sealed class CapturingLogger : ILogger
    {
        readonly string category;
        readonly ConcurrentBag<string> messages;

        public CapturingLogger(string category, ConcurrentBag<string> messages)
        {
            this.category = category;
            this.messages = messages;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);
            if (!string.IsNullOrEmpty(message))
                messages.Add($"[{logLevel}] {category}: {message}");
        }
    }
}

/// <summary>
/// variant of <see cref="JwtAuthFixture"/> that injects a <see cref="CapturingLoggerProvider"/>
/// into the logging pipeline so tests can assert on log output.
/// </summary>
public sealed class JwtAuthWithCapturingLoggerFixture : IDisposable
{
    readonly RSA rsa;
    readonly RsaSecurityKey signingKey;
    readonly WebApplicationFactory<Program> factory;
    readonly System.Net.Http.HttpMessageHandler serverHandler;

    public JwtAuthWithCapturingLoggerFixture(CapturingLoggerProvider capturer)
    {
        rsa        = RSA.Create(2048);
        signingKey = new RsaSecurityKey(rsa) { KeyId = "test-key-capturing" };

        string jwks          = BuildJwks();
        string discoveryJson = BuildDiscoveryDocument();
        string dbPath        = $"/tmp/divoid_cap_{Guid.NewGuid():N}.db3";

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
                    ["Keycloak:UserIdClaimName"]      = "userId"
                });
            });
            builder.ConfigureServices(services => {
                services.AddLogging(logging => logging.AddProvider(capturer));
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options => {
                    options.BackchannelHttpHandler = new JwtAuthFixture.FakeJwksHandlerPublic(
                        JwtAuthFixture.TestAuthority, discoveryJson, jwks);
                    options.TokenValidationParameters.IssuerSigningKey  = signingKey;
                    options.TokenValidationParameters.IssuerSigningKeys = [signingKey];
                });
            });
        });

        serverHandler = factory.Server.CreateHandler();
        EntityManager = factory.Services.GetRequiredService<IEntityManager>();
    }

    public IEntityManager EntityManager { get; }

    public System.Net.Http.HttpClient CreateClient() =>
        new(serverHandler) { BaseAddress = new Uri(TestSetup.BaseUrl) };

    public string MintToken(
        long?     userId    = null,
        string?   audience  = null,
        DateTime? notBefore = null,
        DateTime? expires   = null)
    {
        string aud = audience ?? JwtAuthFixture.TestAudience;
        DateTime now = DateTime.UtcNow;
        var claims = new System.Collections.Generic.List<System.Security.Claims.Claim>();
        if (userId.HasValue)
            claims.Add(new System.Security.Claims.Claim("userId", userId.Value.ToString()));

        var creds = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256);
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer:             JwtAuthFixture.TestAuthority,
            audience:           aud,
            claims:             claims,
            notBefore:          notBefore ?? now.AddSeconds(-5),
            expires:            expires   ?? now.AddMinutes(5),
            signingCredentials: creds);
        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }

    public void Dispose()
    {
        factory.Dispose();
        serverHandler.Dispose();
        rsa.Dispose();
    }

    string BuildJwks()
    {
        RSAParameters p = rsa.ExportParameters(false);
        return JsonSerializer.Serialize(new {
            keys = new[] {
                new {
                    kty = "RSA",
                    use = "sig",
                    kid = signingKey.KeyId,
                    alg = "RS256",
                    n   = Base64UrlEncoder.Encode(p.Modulus!),
                    e   = Base64UrlEncoder.Encode(p.Exponent!)
                }
            }
        });
    }

    static string BuildDiscoveryDocument() =>
        JsonSerializer.Serialize(new {
            issuer                                = JwtAuthFixture.TestAuthority,
            jwks_uri                              = $"{JwtAuthFixture.TestAuthority}/protocol/openid-connect/certs",
            authorization_endpoint                = $"{JwtAuthFixture.TestAuthority}/protocol/openid-connect/auth",
            token_endpoint                        = $"{JwtAuthFixture.TestAuthority}/protocol/openid-connect/token",
            id_token_signing_alg_values_supported = new[] { "RS256" }
        });
}
