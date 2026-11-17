namespace Backend.Services.Teams;

/// <summary>Validates configured Teams identities after schema initialization.</summary>
public sealed class TeamsRoutingValidationService : IHostedService {
    readonly TeamsMessageService transport;

    /// <summary>Creates the startup identity guard.</summary>
    /// <param name="transport">The configured Teams inbox transport.</param>
    public TeamsRoutingValidationService(TeamsMessageService transport) {
        this.transport = transport;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => transport.ValidateBindingAsync();

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
