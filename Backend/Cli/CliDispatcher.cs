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
    /// <returns>process exit code of the verb</returns>
    public static Task<int> RunAsync(string[] args) {
        string verb = args[0];
        switch (verb) {
            case "create-admin":
                return CreateAdminAsync(args[1..]);
            case "backfill-embeddings":
                return BackfillEmbeddingsAsync(args[1..]);
            case "layout-nodes":
                return LayoutNodesAsync();
            default:
                Console.Error.WriteLine($"Unknown verb '{verb}'. Supported verbs: create-admin, backfill-embeddings, layout-nodes");
                return Task.FromResult(1);
        }
    }

    static Task<int> BackfillEmbeddingsAsync(string[] args) {
        long[] ids = null;
        bool longOnly = false;

        for (int i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "--ids" when i + 1 < args.Length && TryParseIds(args[i + 1], out long[] parsed):
                    ids = parsed;
                    i++;
                    break;
                case "--long-only":
                    longOnly = true;
                    break;
                default:
                    Console.Error.WriteLine($"error: unsupported or invalid argument '{args[i]}' for backfill-embeddings. Usage: backfill-embeddings [--ids <id,id,...>] [--long-only]");
                    return Task.FromResult(1);
            }
        }

        return RunWithContainerAsync(
            (services, configuration) => {
                bool embeddingEnabled = configuration["Database:Type"] != "Sqlite";
                services.AddSingleton<IEmbeddingCapability>(new EmbeddingCapability(embeddingEnabled));
                services.AddTransient<EmbeddingBackfillService>();
            },
            async provider => {
                EmbeddingBackfillService backfill = provider.GetRequiredService<EmbeddingBackfillService>();
                ILogger logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("CliDispatcher");

                try {
                    await backfill.RunAsync(ids, longOnly);
                    logger.LogInformation("event=cli.backfill-embeddings exitCode=0");
                    return 0;
                } catch (Exception ex) {
                    Console.Error.WriteLine($"error: {ex.Message}");
                    logger.LogError(ex, "event=cli.backfill-embeddings exitCode=1");
                    return 1;
                }
            });
    }

    static bool TryParseIds(string value, out long[] ids) {
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        long[] parsed = new long[parts.Length];
        for (int i = 0; i < parts.Length; i++) {
            if (!long.TryParse(parts[i], out parsed[i])) {
                ids = null;
                return false;
            }
        }

        ids = parsed;
        return true;
    }

    static Task<int> LayoutNodesAsync() {
        return RunWithContainerAsync(
            (services, _) => services.AddTransient<LayoutNodesService>(),
            async provider => {
                LayoutNodesService layoutService = provider.GetRequiredService<LayoutNodesService>();
                ILogger logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("CliDispatcher");

                try {
                    await layoutService.RunAsync();
                    logger.LogInformation("event=cli.layout-nodes exitCode=0");
                    return 0;
                } catch (Exception ex) {
                    Console.Error.WriteLine($"error: {ex.Message}");
                    logger.LogError(ex, "event=cli.layout-nodes exitCode=1");
                    return 1;
                }
            });
    }


    static Task<int> CreateAdminAsync(string[] args) {
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
            return Task.FromResult(1);
        }

        return RunWithContainerAsync(
            (services, _) => {
                services.AddTransient<IKeyGenerator, KeyGenerator>();
                services.AddTransient<IUserService, UserService>();
                services.AddTransient<IApiKeyService, ApiKeyService>();
            },
            async provider => {
                ILogger logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("CliDispatcher");

                try {
                    IUserService userService = provider.GetRequiredService<IUserService>();
                    IApiKeyService apiKeyService = provider.GetRequiredService<IApiKeyService>();

                    UserDetails user = await userService.CreateUser(new UserParameters { Name = name, Email = email });
                    ApiKeyDetails key = await apiKeyService.CreateApiKey(new ApiKeyParameters {
                        UserId = user.Id,
                        Permissions = ["admin", "write", "read"]
                    });

                    logger.LogInformation("event=cli.create-admin userId={UserId} keyId={KeyId} exitCode=0", user.Id, key.KeyId);

                    Console.WriteLine($"Admin user created:  id={user.Id} name={user.Name}");
                    Console.WriteLine($"Admin key created:   id={key.Id} keyId={key.KeyId}");
                    Console.WriteLine($"Key (store now — unrecoverable): {key.PlaintextKey}");
                    return 0;
                } catch (MissingPepperException ex) {
                    Console.Error.WriteLine($"error: {ex.Message}");
                    logger.LogError("event=cli.create-admin exitCode=1 reason=missing_pepper");
                    return 1;
                } catch (Exception ex) {
                    Console.Error.WriteLine($"error: {ex.Message}");
                    logger.LogError(ex, "event=cli.create-admin exitCode=1");
                    return 1;
                }
            });
    }


    /// <summary>
    /// builds the verb container, initialises the schema and runs <paramref name="job"/> before the provider scope closes
    /// </summary>
    /// <param name="register">adds the verb-specific registrations</param>
    /// <param name="job">the verb's work; its result is the exit code</param>
    /// <returns>exit code of the verb, 1 if the schema could not be initialised</returns>
    static async Task<int> RunWithContainerAsync(Action<IServiceCollection, IConfiguration> register, Func<IServiceProvider, Task<int>> job) {
        IServiceCollection services = new ServiceCollection();

        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        services.AddSingleton(configuration);
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
        services.ConfigureDatabaseService(configuration);
        register(services, configuration);

        await using ServiceProvider provider = services.BuildServiceProvider();

        Init.DatabaseModelService schemaSvc = new(provider.GetRequiredService<Pooshit.Ocelot.Entities.IEntityManager>());
        try {
            await schemaSvc.StartAsync(default);
        } catch (Exception ex) {
            Console.Error.WriteLine($"error: failed to initialise database schema: {ex.Message}");
            return 1;
        }

        return await job(provider);
    }
}
