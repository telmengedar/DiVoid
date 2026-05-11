using System.Text.Json.Serialization;
using Backend.Auth;
using Backend.Errors;
using Backend.Extensions.Startup;
using Backend.Formatters;
using Backend.Init;
using Backend.Services.Auth;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.Services.Users;
using mamgo.services.Binding;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Pooshit.AspNetCore.Services.Errors.Exceptions;
using Pooshit.AspNetCore.Services.Errors.Handlers;
using Pooshit.AspNetCore.Services.Extensions;
using Pooshit.AspNetCore.Services.Formatters;
using Pooshit.AspNetCore.Services.Loggers;
using Pooshit.AspNetCore.Services.Middleware;

namespace Backend;

/// <summary>
/// minimal startup used for mamgo services
/// </summary>
public class Startup
{

    /// <summary>
    /// creates a new <see cref="Startup"/>
    /// </summary>
    /// <param name="configuration">access to configuration</param>
    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    /// <summary>
    /// service configuration
    /// </summary>
    protected IConfiguration Configuration { get; }

    bool AuthEnabled => Configuration.GetValue("Auth:Enabled", true);

    /// <summary>
    /// method used to configure mvc
    /// </summary>
    /// <param name="options">options to modify</param>
    protected virtual void ConfigureMvc(MvcOptions options)
    {
        options.InputFormatters.Insert(0, new JsonInputFormatter());
        options.OutputFormatters.Insert(0, new JsonOutputFormatter());
        options.OutputFormatters.Insert(0, new JsonStreamOutputFormatter());
    }

    /// <summary>
    /// allows for configuration of mvc core builder
    /// </summary>
    /// <param name="coreBuilder">builder used to configure</param>
    protected virtual void ConfigureMvc(IMvcCoreBuilder coreBuilder)
    {

    }

    /// <summary>
    /// configures services known to web api
    /// </summary>
    /// <param name="services">service collection to add services to</param>
    public virtual void ConfigureServices(IServiceCollection services)
    {
        services.AddErrorHandlers();
        // Override the built-in PropertyNotFoundHandler (which maps to 404) and add
        // NotSupportedExceptionHandler; both map PATCH-input errors to HTTP 400.
        // PathQueryParseExceptionHandler maps path-query syntax errors to HTTP 400.
        // Registered after AddErrorHandlers() so they take precedence in the collection.
        services.AddTransient<IErrorHandler, PropertyNotFoundExceptionHandler>();
        services.AddTransient<IErrorHandler, NotSupportedExceptionHandler>();
        services.AddTransient<IErrorHandler, PathQueryParseExceptionHandler>();
        services.AddTransient<IErrorHandler, SemanticSearchUnavailableExceptionHandler>();
        services.AddLogging(options =>
        {
            options.ClearProviders();
            options.AddProvider(new JsonLoggerProvider());
        });

        ConfigureMvc(services.ConfigureMvc(ConfigureMvc));
        services.AddControllers(o =>
        {
            o.ModelBinderProviders.Insert(0, new ArrayParameterBinderProvider());
        }).AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        services.ConfigureDatabaseService(Configuration);

        bool embeddingEnabled = Configuration["Database:Type"] != "Sqlite";
        services.AddSingleton<IEmbeddingCapability>(new EmbeddingCapability(embeddingEnabled));

        services.AddTransient<INodeService, NodeService>();
        services.AddTransient<IKeyGenerator, KeyGenerator>();
        services.AddTransient<IUserService, UserService>();
        services.AddTransient<IApiKeyService, ApiKeyService>();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

        if (AuthEnabled) {
            // Fail-closed: Keycloak:Audience must be set when auth is enabled.
            // An empty audience would cause the JwtBearer handler to accept tokens
            // intended for other Keycloak clients.
            string audience = Configuration["Keycloak:Audience"] ?? "";
            if (string.IsNullOrWhiteSpace(audience))
                throw new MissingAudienceException(
                    "Keycloak:Audience is empty. The service will not start with Auth:Enabled=true without a configured audience. " +
                    "Set Keycloak:Audience to the DiVoid Keycloak client_id in the environment-specific appsettings override.");

            string authority = Configuration["Keycloak:Authority"] ?? "https://auth.mamgo.io/realms/master";
            bool requireHttpsMetadata = Configuration.GetValue("Keycloak:RequireHttpsMetadata", false);

            services.AddAuthentication(options => {
                // JwtBearer is the default: it is tried first on every request.
                // API-key tokens do not look like JWTs (no three-segment base64url form)
                // so JwtBearer returns NoResult for them and the fallback policy
                // then tries the ApiKey scheme.
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options => {
                options.Authority = authority;
                options.RequireHttpsMetadata = requireHttpsMetadata;

                // IMPORTANT: never log raw JWT contents — only issuer, audience, and
                // the resolved DiVoid userId should appear in logs.
                options.TokenValidationParameters = new TokenValidationParameters {
                    ValidateIssuer = true,
                    ValidIssuer = authority,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(2),
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    RequireExpirationTime = true
                };

                // Abstain (NoResult) for tokens that do not look like JWTs.
                // A valid JWT compact serialization has exactly 2 dots (header.payload.signature).
                // API-key tokens have the form "<keyId>.<secret>" — exactly 1 dot — and must be
                // routed to the ApiKey scheme instead of failing JwtBearer validation with an error.
                // Clearing ctx.Token causes JwtBearerHandler to return NoResult, giving ApiKey
                // a chance. See design doc section 6.3.
                options.Events = new JwtBearerEvents {
                    OnMessageReceived = ctx => {
                        string token = ctx.Token ?? "";
                        // Count dots; a JWT needs exactly 2 (compact serialization: h.p.s)
                        int dotCount = 0;
                        foreach (char c in token) {
                            if (c == '.') dotCount++;
                        }
                        if (dotCount != 2)
                            ctx.Token = string.Empty; // causes handler to return NoResult
                        return Task.CompletedTask;
                    }
                };
            })
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName, null);

            services.AddTransient<IClaimsTransformation, KeycloakClaimsTransformation>();

            services.AddAuthorization(options => {
                // All named policies include both authentication schemes so the authorization
                // middleware tries JwtBearer first, then ApiKey, for every protected endpoint.
                // Without this, endpoints with [Authorize(Policy="read")] only invoke the
                // DefaultAuthenticateScheme (JwtBearer) and API-key callers get 401.
                options.AddPolicy("admin", p => p
                    .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, ApiKeyAuthenticationHandler.SchemeName)
                    .AddRequirements(new PermissionRequirement("admin")));
                options.AddPolicy("write",  p => p
                    .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, ApiKeyAuthenticationHandler.SchemeName)
                    .AddRequirements(new PermissionRequirement("write")));
                options.AddPolicy("read",   p => p
                    .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, ApiKeyAuthenticationHandler.SchemeName)
                    .AddRequirements(new PermissionRequirement("read")));

                // Fallback: require authentication on every endpoint without explicit [AllowAnonymous].
                options.FallbackPolicy = new AuthorizationPolicyBuilder()
                    .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, ApiKeyAuthenticationHandler.SchemeName)
                    .RequireAuthenticatedUser()
                    .Build();
            });
        } else {
            services.AddAuthorization(options => {
                options.AddPolicy("admin", p => p.RequireAssertion(_ => true));
                options.AddPolicy("write",  p => p.RequireAssertion(_ => true));
                options.AddPolicy("read",   p => p.RequireAssertion(_ => true));
                // No fallback — all endpoints open when auth is disabled
            });
        }

        services.AddHostedService<DatabaseModelService>();
        services.AddHostedService<StartupWarningService>();
    }

    /// <summary>
    /// configures service pipeline
    /// </summary>
    /// <param name="app">app to add middlewares to</param>
    /// <param name="env">environment information</param>
    public virtual void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseRouting();
        app.UseMiddleware<ErrorHandlerMiddleware>();

        if (AuthEnabled)
            app.UseAuthentication();
        app.UseAuthorization();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
        });
    }
}
