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

            // PolicyScheme acts as a single entry point for the authentication pipeline.
            // It inspects the inbound bearer token shape and forwards to exactly one scheme:
            //   - 3-dot-separated base64url (compact JWT) → JwtBearer
            //   - anything else (API-key format "<keyId>.<secret>", missing, or malformed) → ApiKey
            // This means only one scheme ever runs per request, eliminating the "ApiKey was
            // forbidden" log noise that the previous multi-scheme-per-policy arrangement caused
            // (the framework called ForbidAsync on every scheme listed in a policy, not just the
            // one that authenticated the request).
            const string CombinedScheme = "DiVoidBearer";

            services.AddAuthentication(CombinedScheme)
            .AddPolicyScheme(CombinedScheme, "JWT or ApiKey bearer", o => {
                o.ForwardDefaultSelector = ctx => {
                    string authHeader = ctx.Request.Headers.Authorization.ToString();
                    if (string.IsNullOrEmpty(authHeader) ||
                        !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        return ApiKeyAuthenticationHandler.SchemeName; // no/non-Bearer header → ApiKey produces clean 401
                    string token = authHeader.Substring("Bearer ".Length).Trim();
                    // Count dots: a compact JWT serialisation has exactly 2 (header.payload.signature).
                    int dotCount = 0;
                    foreach (char c in token) if (c == '.') dotCount++;
                    return dotCount == 2
                        ? JwtBearerDefaults.AuthenticationScheme
                        : ApiKeyAuthenticationHandler.SchemeName;
                };
            })
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options => {
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
                // Note: the OnMessageReceived dot-count gate that existed here previously has been
                // removed. The PolicyScheme ForwardDefaultSelector is now the primary gate —
                // non-JWT tokens never reach JwtBearer. ApiKeyAuthenticationHandler retains its
                // own symmetric dot-count guard as defence-in-depth.
            })
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName, null);

            services.AddTransient<IClaimsTransformation, KeycloakClaimsTransformation>();

            services.AddAuthorization(options => {
                // Policies carry no explicit scheme list: the PolicyScheme (DiVoidBearer) is the
                // default authenticate scheme and dispatches to exactly one sub-scheme per request.
                // Listing individual schemes here would cause the framework to ForbidAsync each
                // listed scheme on a 403, producing spurious "ApiKey was forbidden" log lines.
                options.AddPolicy("admin", p => p.AddRequirements(new PermissionRequirement("admin")));
                options.AddPolicy("write",  p => p.AddRequirements(new PermissionRequirement("write")));
                options.AddPolicy("read",   p => p.AddRequirements(new PermissionRequirement("read")));

                // Fallback: require authentication on every endpoint without explicit [AllowAnonymous].
                options.FallbackPolicy = new AuthorizationPolicyBuilder()
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
