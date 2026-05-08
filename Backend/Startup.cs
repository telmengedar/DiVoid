using System.Text.Json.Serialization;
using Backend.Auth;
using Backend.Errors;
using Backend.Extensions.Startup;
using Backend.Formatters;
using Backend.Init;
using Backend.Services.Auth;
using Backend.Services.Nodes;
using Backend.Services.Users;
using mamgo.services.Binding;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
        // Registered after AddErrorHandlers() so they take precedence in the collection.
        services.AddTransient<IErrorHandler, PropertyNotFoundExceptionHandler>();
        services.AddTransient<IErrorHandler, NotSupportedExceptionHandler>();
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

        services.AddTransient<INodeService, NodeService>();
        services.AddTransient<IKeyGenerator, KeyGenerator>();
        services.AddTransient<IUserService, UserService>();
        services.AddTransient<IApiKeyService, ApiKeyService>();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

        if (AuthEnabled) {
            services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                        ApiKeyAuthenticationHandler.SchemeName, null);

            services.AddAuthorization(options => {
                options.AddPolicy("admin", p => p.AddRequirements(new PermissionRequirement("admin")));
                options.AddPolicy("write",  p => p.AddRequirements(new PermissionRequirement("write")));
                options.AddPolicy("read",   p => p.AddRequirements(new PermissionRequirement("read")));

                // Fallback: require authentication on every endpoint without explicit [AllowAnonymous]
                options.FallbackPolicy = new AuthorizationPolicyBuilder()
                    .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
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
