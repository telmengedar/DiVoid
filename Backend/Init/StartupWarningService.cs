using System;
using System.Threading;
using System.Threading.Tasks;
using Backend.Services.Auth;
using Backend.Services.Embeddings;
using Backend.Services.Users;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backend.Init;

/// <summary>
/// emits startup warnings when the service is missing operational prerequisites
/// (no admin key, no admin email contact)
/// </summary>
public class StartupWarningService : IHostedService {
    readonly IApiKeyService apiKeyService;
    readonly IUserService userService;
    readonly IEmbeddingProvider embeddingProvider;
    readonly ILogger<StartupWarningService> logger;

    public StartupWarningService(
        IApiKeyService apiKeyService,
        IUserService userService,
        IEmbeddingProvider embeddingProvider,
        ILogger<StartupWarningService> logger) {
        this.apiKeyService = apiKeyService;
        this.userService = userService;
        this.embeddingProvider = embeddingProvider;
        this.logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken) {
        if (embeddingProvider.IsEnabled)
            logger.LogDebug("embedding provider={Provider} dimension={Dimension} — embedding generation enabled",
                embeddingProvider.GetType().Name, embeddingProvider.Dimension);
        else
            logger.LogDebug("embedding provider=None — embedding generation disabled");

        try {
            if (!await apiKeyService.AnyAdminKeyExists())
                logger.LogWarning("no admin api key exists — no one can administer this instance until one is created via the create-admin CLI");

            if (!await userService.AnyAdminHasEmail())
                logger.LogWarning("no admin user has an email — there is no contact path if this instance needs to be reached out about");
        } catch (Exception ex) {
            // Startup warnings must never block startup
            logger.LogError(ex, "startup warning checks failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
