using System.Text.Json.Serialization;
using Backend.Extensions.Startup;
using Backend.Formatters;
using Backend.Init;
using Backend.Services.Nodes;
using mamgo.services.Binding;
using Microsoft.AspNetCore.Mvc;
using Pooshit.AspNetCore.Services.Extensions;
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

    /// <summary>
    /// method used to configure mvc
    /// </summary>
    /// <param name="options">options to modify</param>
    protected virtual void ConfigureMvc(MvcOptions options)
    {
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
        services.AddLogging(options =>
        {
            options.ClearProviders();
            // task logger is losing messages ... not sure why - for now use the regular logger
            options.AddProvider(new JsonLoggerProvider());
        });

        ConfigureMvc(services.ConfigureMvc(ConfigureMvc));
        services.AddControllers(o =>
        {
            o.ModelBinderProviders.Insert(0, new ArrayParameterBinderProvider());
        }).AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        services.ConfigureDatabaseService(Configuration);

        services.AddTransient<INodeService, NodeService>();

        services.AddHostedService<DatabaseModelService>();
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

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
        });
    }
}