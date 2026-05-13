using System;
using System.Threading.Tasks;
using Backend.Extensions.Startup;
using Backend.Models.Auth;
using Backend.Models.Users;
using Backend.Services.Auth;
using Backend.Services.Embeddings;
using Backend.Services.Layout;
using Backend.Services.Users;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Backend.Cli;

/// <summary>
/// dispatches CLI verbs invoked when the binary is launched with arguments
/// </summary>
public static class CliDispatcher {

    /// <summary>
    /// runs the CLI verb indicated by <paramref name="args"/>
    /// </summary>
    /// <param name="args">command-line arguments; first element is the verb</param>
    public static async Task RunAsync(string[] args) {
        string verb = args[0];
        switch (verb) {
            case "create-admin":
                await CreateAdminAsync(args[1..]);
                break;
            case "backfill-embeddings":
                await BackfillEmbeddingsAsync();
                break;
            case "layout-nodes":
                await LayoutNodesAsync();
                break;
            default:
                Console.Error.WriteLine($"Unknown verb '{verb}'. Supported verbs: create-admin, backfill-embeddings, layout-nodes");
                Environment.Exit(1);
                break;
        }
    }

    static async Task BackfillEmbeddingsAsync() {
        IServiceCollection services = new ServiceCollection();

        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        services.AddSingleton(configuration);
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
        services.ConfigureDatabaseService(configuration);

        bool embeddingEnabled = configuration["Database:Type"] != "Sqlite";
        services.AddSingleton<IEmbeddingCapability>(new EmbeddingCapability(embeddingEnabled));
        services.AddTransient<EmbeddingBackfillService>();

        await using ServiceProvider provider = services.BuildServiceProvider();

        // Ensure schema exists
        Init.DatabaseModelService schemaSvc = new(provider.GetRequiredService<Pooshit.Ocelot.Entities.IEntityManager>());
        try {
            await schemaSvc.StartAsync(default);
        } catch (Exception ex) {
            Console.Error.WriteLine($"error: failed to initialise database schema: {ex.Message}");
            Environment.Exit(1);
            return;
        }

        EmbeddingBackfillService backfill = provider.GetRequiredService<EmbeddingBackfillService>();
        ILogger logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("CliDispatcher");

        try {
            await backfill.RunAsync();
            logger.LogInformation("event=cli.backfill-embeddings exitCode=0");
            Environment.Exit(0);
        } catch (Exception ex) {
            Console.Error.WriteLine($"error: {ex.Message}");
            logger.LogError(ex, "event=cli.backfill-embeddings exitCode=1");
            Environment.Exit(1);
        }
    }

    static async Task LayoutNodesAsync() {
        IServiceCollection services = new ServiceCollection();

        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        services.AddSingleton(configuration);
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
        services.ConfigureDatabaseService(configuration);
        services.AddTransient<LayoutNodesService>();

        await using ServiceProvider provider = services.BuildServiceProvider();

        // Ensure schema exists
        Init.DatabaseModelService schemaSvc = new(provider.GetRequiredService<Pooshit.Ocelot.Entities.IEntityManager>());
        try {
            await schemaSvc.StartAsync(default);
        } catch (Exception ex) {
            Console.Error.WriteLine($"error: failed to initialise database schema: {ex.Message}");
            Environment.Exit(1);
            return;
        }

        LayoutNodesService layoutService = provider.GetRequiredService<LayoutNodesService>();
        ILogger logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("CliDispatcher");

        try {
            await layoutService.RunAsync();
            logger.LogInformation("event=cli.layout-nodes exitCode=0");
            Environment.Exit(0);
        } catch (Exception ex) {
            Console.Error.WriteLine($"error: {ex.Message}");
            logger.LogError(ex, "event=cli.layout-nodes exitCode=1");
            Environment.Exit(1);
        }
    }


    static async Task CreateAdminAsync(string[] args) {
        string name = null;
        string email = null;

        for (int i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "--name" when i + 1 < args.Length:
                    name = args[++i];
                    break;
                case "--email" when i + 1 < args.Length:
                    email = args[++i];
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(name)) {
            Console.Error.WriteLine("error: --name is required for create-admin");
            Environment.Exit(1);
            return;
        }

        IServiceCollection services = new ServiceCollection();

        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        services.AddSingleton(configuration);
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
        services.ConfigureDatabaseService(configuration);

        // Register auth services — ApiKeyService validates pepper in its constructor
        services.AddTransient<IKeyGenerator, KeyGenerator>();
        services.AddTransient<IUserService, UserService>();
        services.AddTransient<IApiKeyService, ApiKeyService>();

        await using ServiceProvider provider = services.BuildServiceProvider();

        // Ensure schema exists
        Init.DatabaseModelService schemaSvc = new(provider.GetRequiredService<Pooshit.Ocelot.Entities.IEntityManager>());
        try {
            await schemaSvc.StartAsync(default);
        } catch (Exception ex) {
            Console.Error.WriteLine($"error: failed to initialise database schema: {ex.Message}");
            Environment.Exit(1);
            return;
        }

        IUserService userService = provider.GetRequiredService<IUserService>();
        IApiKeyService apiKeyService = provider.GetRequiredService<IApiKeyService>();
        ILogger logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("CliDispatcher");

        try {
            UserDetails user = await userService.CreateUser(new UserParameters { Name = name, Email = email });
            ApiKeyDetails key = await apiKeyService.CreateApiKey(new ApiKeyParameters {
                UserId = user.Id,
                Permissions = ["admin", "write", "read"]
            });

            logger.LogInformation("event=cli.create-admin userId={UserId} keyId={KeyId} exitCode=0", user.Id, key.KeyId);

            Console.WriteLine($"Admin user created:  id={user.Id} name={user.Name}");
            Console.WriteLine($"Admin key created:   id={key.Id} keyId={key.KeyId}");
            Console.WriteLine($"Key (store now — unrecoverable): {key.PlaintextKey}");
            Environment.Exit(0);
        } catch (MissingPepperException ex) {
            Console.Error.WriteLine($"error: {ex.Message}");
            logger.LogError("event=cli.create-admin exitCode=1 reason=missing_pepper");
            Environment.Exit(1);
        } catch (Exception ex) {
            Console.Error.WriteLine($"error: {ex.Message}");
            logger.LogError(ex, "event=cli.create-admin exitCode=1");
            Environment.Exit(1);
        }
    }
}
