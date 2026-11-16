using Backend.Extensions.Startup;

namespace Backend.Services.Teams;

/// <summary>The optional fixed inbox binding for the configured Teams bot.</summary>
public sealed class TeamsRoutingOptions {
    /// <summary>Validates the optional routing configuration.</summary>
    /// <param name="configuration">The integration configuration.</param>
    public TeamsRoutingOptions(IConfiguration configuration) {
        BotAppId = Guid.TryParse(configuration["AzureAd:ClientId"], out Guid bot) ? bot.ToString("D") : configuration["AzureAd:ClientId"];
        Enabled = configuration.GetValue<bool>("Teams:Enabled");
        if (!Enabled)
            return;
        AllowedTenantIds = TeamsExtensions.AllowedTenants(configuration);
        string user = configuration["Teams:Target:UserId"];
        string bridge = configuration["Teams:BridgeUserId"];
        Tag = configuration["Teams:Target:Tag"];
        IsBound = user != null || bridge != null || Tag != null;
        if (!IsBound)
            return;
        if (!Enabled || !configuration.GetValue("Auth:Enabled", true) ||
            !long.TryParse(user, out long recipientId) || recipientId <= 0 ||
            !long.TryParse(bridge, out long bridgeId) || bridgeId <= 0 || recipientId == bridgeId ||
            string.IsNullOrWhiteSpace(Tag) || Tag.Length > 128 ||
            Tag.Any(character => char.IsWhiteSpace(character) || "[],".Contains(character)))
            throw new InvalidOperationException("Teams routing requires authentication, a distinct positive target/bridge user pair and one valid receiving tag.");
        RecipientId = recipientId;
        BridgeUserId = bridgeId;
    }

    /// <summary>Whether the Teams integration is enabled.</summary>
    public bool Enabled { get; }

    /// <summary>Whether the fixed inbox binding is configured.</summary>
    public bool IsBound { get; }

    /// <summary>The configured bot application identifier.</summary>
    public string BotAppId { get; }

    /// <summary>The optional allowed activity tenants, independent of credential authority.</summary>
    public IReadOnlySet<Guid> AllowedTenantIds { get; } = new HashSet<Guid>();

    /// <summary>The configured inbox recipient.</summary>
    public long RecipientId { get; }

    /// <summary>The disabled credentialless bridge author.</summary>
    public long BridgeUserId { get; }

    /// <summary>The receiving session tag.</summary>
    public string Tag { get; }
}
