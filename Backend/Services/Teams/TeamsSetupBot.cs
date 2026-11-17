using Backend.Extensions.Startup;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.Clients;

namespace Backend.Services.Teams;

/// <summary>Replies to authenticated setup messages through the Teams SDK.</summary>
public sealed class TeamsSetupBot : TeamsBotApplication {
    /// <summary>Creates the setup message handler.</summary>
    /// <param name="api">The native Teams API client.</param>
    /// <param name="accessor">The current HTTP request accessor.</param>
    /// <param name="logger">The SDK logger.</param>
    /// <param name="options">The bot application settings.</param>
    /// <param name="diagnostics">The application diagnostic logger factory.</param>
    public TeamsSetupBot(ApiClient api, IHttpContextAccessor accessor, ILogger<TeamsBotApplication> logger,
        TeamsBotApplicationOptions options, ILoggerFactory diagnostics) : base(api, accessor, logger, options) {
        ILogger events = diagnostics.CreateLogger(TeamsExtensions.DiagnosticsCategory);
        this.OnMessage(async (context, cancellationToken) => {
            if (string.IsNullOrWhiteSpace(context.Activity.Text))
                return;
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, accessor.HttpContext.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            string request = accessor.HttpContext.TraceIdentifier;
            events.LogInformation("event=teams.setup.send_started request={Request}", request);
            try {
                await context.SendAsync("DiVoid Teams webhook is connected. Message routing to DiVoid users is not implemented yet.", timeout.Token);
                events.LogInformation("event=teams.setup.send_accepted request={Request}", request);
            } catch (Exception exception) {
                string category = exception is OperationCanceledException or TimeoutException ? "cancelled_or_timeout" : "send_failed";
                events.LogWarning("event=teams.setup.send_failed request={Request} reason={Reason}", request, category);
                throw;
            }
        });
    }
}
