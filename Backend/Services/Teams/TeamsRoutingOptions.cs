using Backend.Extensions.Startup;

namespace Backend.Services.Teams;

/// <summary>The server-controlled Teams transport identity and tenant policy.</summary>
public sealed class TeamsRoutingOptions {
    /// <summary>Validates transport settings and rejects competing legacy routing authority.</summary>
    /// <param name="configuration">The transport configuration.</param>
    public TeamsRoutingOptions(IConfiguration configuration) {
        BotAppId = Guid.TryParse(configuration["AzureAd:ClientId"], out Guid bot) ? bot.ToString("D") : configuration["AzureAd:ClientId"];
        Enabled = configuration.GetValue<bool>("Teams:Enabled");
        foreach (string field in new[] { "Teams:Target:UserId", "Teams:Target:Tag", "Teams:BridgeUserId" })
            if (!string.IsNullOrEmpty(configuration[field]))
                throw new InvalidOperationException($"Remove legacy routing field {field}; Teams routing is configured in the graph.");
        if (Enabled && !configuration.GetValue("Auth:Enabled", true))
            throw new InvalidOperationException("Teams graph routing requires Auth:Enabled=true to preserve graph write authority.");
        if (Enabled)
            AllowedTenantIds = TeamsExtensions.AllowedTenants(configuration);
    }

    /// <summary>Whether the Teams transport is enabled.</summary>
    public bool Enabled { get; }

    /// <summary>The configured and normalized bot application identifier.</summary>
    public string BotAppId { get; }

    /// <summary>The optional activity-tenant restriction, independent of credential authority.</summary>
    public IReadOnlySet<Guid> AllowedTenantIds { get; } = new HashSet<Guid>();
}
