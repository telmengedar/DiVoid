using System.Text.Json;
using System.Text;
using Pooshit.Json;
using Backend.Services.Teams;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Routing;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Identity.Abstractions;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.Clients;
using Microsoft.Teams.Apps.Schema;
using Microsoft.Teams.Core;
using Microsoft.Teams.Core.Hosting;
using Microsoft.Teams.Core.Schema;

namespace Backend.Extensions.Startup;

/// <summary>Registers the isolated public Azure Teams setup webhook.</summary>
public static class TeamsExtensions {
    const string Scheme = "TeamsBot";
    const string CredentialTenant = "botframework.com";
    const string CredentialInstance = "https://login.microsoftonline.com/";
    const string Issuer = "https://api.botframework.com";
    const string Metadata = "https://login.botframework.com/v1/.well-known/openid-configuration";
    const int MaximumBodyBytes = 256 * 1024;
    static readonly object endorsement = new();

    /// <summary>Registers optional Teams authentication and SDK clients.</summary>
    /// <param name="services">The application service registrations.</param>
    /// <param name="configuration">The integration configuration.</param>
    /// <returns>The application service registrations.</returns>
    public static IServiceCollection AddTeamsWebhook(this IServiceCollection services, IConfiguration configuration) {
        if (!configuration.GetValue<bool>("Teams:Enabled"))
            return services;

        ValidateConfiguration(configuration);
        AllowedTenants(configuration);
        string clientId = configuration["AzureAd:ClientId"];
        services.AddAuthentication().AddJwtBearer(Scheme, options => {
            options.MetadataAddress = Metadata;
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters {
                AuthenticationType = Scheme,
                ValidateIssuer = true,
                ValidIssuer = Issuer,
                ValidateAudience = true,
                ValidAudience = clientId,
                IgnoreTrailingSlashWhenValidatingAudience = false,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ClockSkew = TimeSpan.FromMinutes(2)
            };
            options.Events = new JwtBearerEvents {
                OnTokenValidated = async context => {
                    Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration keys =
                        await context.Options.ConfigurationManager.GetConfigurationAsync(context.HttpContext.RequestAborted);
                    SecurityKey signingKey = context.SecurityToken.SigningKey;
                    JsonWebKey key = keys.JsonWebKeySet?.Keys.FirstOrDefault(candidate =>
                        candidate.Kid == signingKey.KeyId &&
                        candidate.ComputeJwkThumbprint().SequenceEqual(signingKey.ComputeJwkThumbprint()));
                    context.HttpContext.Items[endorsement] = key != null &&
                        key.AdditionalData.TryGetValue("endorsements", out object endorsements) &&
                        endorsements is JsonElement { ValueKind: JsonValueKind.Array } values &&
                        values.EnumerateArray().Any(value =>
                            value.ValueKind == JsonValueKind.String && value.GetString() == "msteams");
                }
            };
        });
        services.AddAuthorization(options => options.AddPolicy(Scheme, policy =>
            policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser()));
        services.AddHttpContextAccessor();
        services.AddLogging(logging => logging.AddFilter("Microsoft.Teams", LogLevel.None));
        BotConfig botConfig = BotConfig.Resolve(services);
        services.EnsureMsalServices(botConfig);
        services.AddBotClient<ConversationClient>(nameof(ConversationClient), botConfig);
        services.AddBotClient<UserTokenClient>(nameof(UserTokenClient), botConfig);
        services.AddBotHttpClient(nameof(ApiClient), botConfig);
        services.AddTeamsApiClient(nameof(ApiClient));
        services.PostConfigure<MicrosoftIdentityApplicationOptions>("AzureAd", options => {
            options.Instance = CredentialInstance;
            options.TenantId = CredentialTenant;
            options.Authority = string.Empty;
        });
        services.AddSingleton(new TeamsBotApplicationOptions {
            AppId = clientId,
            ProcessActivityTimeout = TimeSpan.FromSeconds(15)
        });
        services.AddSingleton<TeamsSetupBot>();
        return services;
    }

    static void ValidateConfiguration(IConfiguration configuration) {
        HashSet<string> allowed = new(StringComparer.OrdinalIgnoreCase) {
            "AzureAd:TenantId", "AzureAd:ClientId", "AzureAd:Instance",
            "AzureAd:DangerouslyAllowUnauthenticatedRequests",
            "AzureAd:ClientCredentials:0:SourceType", "AzureAd:ClientCredentials:0:ClientSecret",
            "BotFramework:OpenIdMetadataUrl", "BotFramework:BotTokenIssuer", "BotFramework:GraphBaseUrl"
        };
        foreach (KeyValuePair<string, string> setting in configuration.AsEnumerable().Where(setting =>
            setting.Value != null && (setting.Key.StartsWith("AzureAd:", StringComparison.OrdinalIgnoreCase) ||
                setting.Key.StartsWith("BotFramework:", StringComparison.OrdinalIgnoreCase))))
            if (!allowed.Contains(setting.Key))
                throw new InvalidOperationException($"Teams does not support configuration field {setting.Key}.");
        if (!Guid.TryParse(configuration["AzureAd:ClientId"], out Guid id) || id == Guid.Empty)
            throw new InvalidOperationException("AzureAd:ClientId must be a nonempty GUID when Teams is enabled.");
        IConfigurationSection[] credentials = configuration.GetSection("AzureAd:ClientCredentials").GetChildren().ToArray();
        if (credentials.Length != 1 || credentials[0]["SourceType"] != "ClientSecret" ||
            string.IsNullOrWhiteSpace(credentials[0]["ClientSecret"]))
            throw new InvalidOperationException("Teams requires one AzureAd:ClientCredentials ClientSecret credential.");
        foreach ((string field, string expected) in new[] {
            ("AzureAd:Instance", CredentialInstance),
            ("AzureAd:TenantId", CredentialTenant),
            ("BotFramework:OpenIdMetadataUrl", Metadata),
            ("BotFramework:BotTokenIssuer", Issuer),
            ("BotFramework:GraphBaseUrl", "https://graph.microsoft.com")
        })
            if (configuration[field] is string value && value != expected)
                throw new InvalidOperationException($"Teams does not support overriding {field} for this public Azure profile.");
        foreach (string field in new[] { "AzureAd:DangerouslyAllowUnauthenticatedRequests", "Teams:DangerouslyAllowUnauthenticatedRequests" })
            if (configuration[field] is string value && (!bool.TryParse(value, out bool bypass) || bypass))
                throw new InvalidOperationException("Teams authentication cannot be disabled.");
    }

    static HashSet<Guid> AllowedTenants(IConfiguration configuration) {
        IConfigurationSection section = configuration.GetSection("Teams:AllowedTenantIds");
        if (section.Value != null)
            throw new InvalidOperationException("Teams:AllowedTenantIds must be an array of tenant GUIDs.");
        HashSet<Guid> tenants = new();
        foreach (IConfigurationSection entry in section.GetChildren()) {
            if (!Guid.TryParse(entry.Value, out Guid tenant) || tenant == Guid.Empty)
                throw new InvalidOperationException("Teams:AllowedTenantIds entries must be nonempty tenant GUIDs.");
            tenants.Add(tenant);
        }
        return tenants;
    }

    /// <summary>Maps the optional Teams activity endpoint.</summary>
    /// <param name="endpoints">The application endpoint registrations.</param>
    /// <param name="configuration">The integration configuration.</param>
    public static void MapTeamsWebhook(this IEndpointRouteBuilder endpoints, IConfiguration configuration) {
        if (!configuration.GetValue<bool>("Teams:Enabled"))
            return;
        HashSet<Guid> tenants = AllowedTenants(configuration);
        endpoints.MapPost("/api/teams/messages", (HttpContext context, TeamsSetupBot bot) =>
            ProcessAsync(context, bot, tenants)).RequireAuthorization(Scheme);
    }

    static async Task<IResult> ProcessAsync(HttpContext context, TeamsSetupBot bot, HashSet<Guid> tenants) {
        if (!context.Items.TryGetValue(endorsement, out object endorsed) || endorsed is not true)
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (context.Request.ContentLength > MaximumBodyBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        using MemoryStream body = new();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) != 0) {
            if (body.Length + read > MaximumBodyBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            await body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
        }
        TeamsActivity activity;
        CoreActivity received;
        try {
            string json = new UTF8Encoding(false, true).GetString(body.ToArray());
            received = CoreActivity.FromJsonString(json);
            Dictionary<string, object> fields = Json.Read<Dictionary<string, object>>(json);
            if (fields == null || !fields.TryGetValue("type", out object type) ||
                type is not string text || string.IsNullOrWhiteSpace(text))
                return Results.BadRequest();
            activity = TeamsActivity.FromActivity(received);
            if (string.IsNullOrWhiteSpace(activity.Id))
                return Results.BadRequest();
        } catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or InvalidOperationException) {
            return Results.BadRequest();
        }
        if (activity.ChannelId != "msteams" ||
            !Guid.TryParse(activity.ChannelData?.Tenant?.Id, out Guid activityTenant) || activityTenant == Guid.Empty ||
            (tenants.Count > 0 && !tenants.Contains(activityTenant)) ||
            (activity.Conversation?.TenantId is string conversationTenant &&
                (!Guid.TryParse(conversationTenant, out Guid parsedTenant) || parsedTenant != activityTenant)))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        Uri serviceUrl = activity.ServiceUrl;
        string[] claims = context.User.Claims.Where(claim => claim.Type is "serviceUrl" or "serviceurl")
            .Select(claim => claim.Value).ToArray();
        if (serviceUrl == null || !serviceUrl.IsAbsoluteUri || serviceUrl.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(serviceUrl.UserInfo) || !string.IsNullOrEmpty(serviceUrl.Query) ||
            !string.IsNullOrEmpty(serviceUrl.Fragment) || claims.Length == 0 ||
            claims.Any(claim => string.IsNullOrWhiteSpace(claim) || claim != serviceUrl.OriginalString))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (activity.Type == "invoke")
            return Results.StatusCode(StatusCodes.Status501NotImplemented);
        if (activity is not MessageActivity message || string.IsNullOrWhiteSpace(message.Text))
            return Results.Ok();
        if (string.IsNullOrWhiteSpace(activity.Conversation?.Id) || string.IsNullOrWhiteSpace(activity.From?.Id) ||
            string.IsNullOrWhiteSpace(activity.Recipient?.Id))
            return Results.BadRequest();
        TeamsRoutingOptions routing = context.RequestServices.GetRequiredService<TeamsRoutingOptions>();
        if (routing.IsBound) {
            await context.RequestServices.GetRequiredService<TeamsMessageService>().ReceiveAsync(message, context.RequestAborted);
            return Results.Ok();
        }
        try {
            await bot.ProcessAsync(received, context.User, null, context.RequestAborted);
            return Results.Ok();
        } catch (BotHandlerException exception) {
            return Results.StatusCode(exception.InnerException is OperationCanceledException or TimeoutException
                ? StatusCodes.Status504GatewayTimeout : StatusCodes.Status502BadGateway);
        }
    }
}
