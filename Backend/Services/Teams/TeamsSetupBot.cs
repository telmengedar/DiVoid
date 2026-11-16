using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.Clients;

namespace Backend.Services.Teams;

/// <summary>Replies to authenticated setup messages through the Teams SDK.</summary>
public sealed class TeamsSetupBot : TeamsBotApplication {
    /// <summary>Creates the setup message handler.</summary>
    public TeamsSetupBot(ApiClient api, IHttpContextAccessor accessor, ILogger<TeamsBotApplication> logger,
        TeamsBotApplicationOptions options) : base(api, accessor, logger, options) {
        this.OnMessage(async (context, cancellationToken) => {
            if (string.IsNullOrWhiteSpace(context.Activity.Text))
                return;
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, accessor.HttpContext.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await context.SendAsync("DiVoid Teams webhook is connected. Message routing to DiVoid users is not implemented yet.", timeout.Token);
        });
    }
}
