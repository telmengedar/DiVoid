using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Pooshit.Http.Paths;
using Pooshit.Json;

namespace Backend.tests.Fixtures;

/// <summary>Serves synthetic identity discovery and intercepts native token requests without external I/O.</summary>
/// <param name="tenant">The expected credential tenant.</param>
public sealed class NativeAuthorityBoundary(string tenant = "botframework.com") : HttpMessageHandler {
    /// <summary>The captured authority allow-list decisions.</summary>
    public ConcurrentQueue<bool> Observed { get; } = new();

    /// <summary>The number of token requests intercepted in memory.</summary>
    public int TokenRequests { get; private set; }

    /// <summary>Whether the token request used the Connector scope.</summary>
    public bool TokenScopeMatches { get; private set; }

    /// <summary>Whether the request used the sole configured synthetic client credential.</summary>
    public bool CredentialMatches { get; private set; }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        Uri? uri = request.RequestUri;
        bool allowed = uri is { Scheme: "https", Host: "login.microsoftonline.com", Port: 443 } &&
            (uri.AbsoluteUri.StartsWith(Rest.Path("https://login.microsoftonline.com", tenant, string.Empty), StringComparison.Ordinal) ||
                uri.AbsolutePath == "/common/discovery/instance");
        Observed.Enqueue(allowed);
        if (!allowed)
            throw new InvalidOperationException("The synthetic authority boundary was reached.");
        if (request.Method == HttpMethod.Get && uri!.AbsolutePath == "/common/discovery/instance")
            return Metadata(new Dictionary<string, object> {
                ["tenant_discovery_endpoint"] = Rest.Path("https://login.microsoftonline.com", tenant, "v2.0", ".well-known", "openid-configuration"),
                ["metadata"] = new[] { new {
                    preferred_network = "login.microsoftonline.com", preferred_cache = "login.microsoftonline.com", aliases = new[] { "login.microsoftonline.com" }
                } }
            });
        if (request.Method == HttpMethod.Get && uri!.AbsolutePath.TrimStart('/') == Rest.Path(tenant, "v2.0", ".well-known", "openid-configuration"))
            return Metadata(new Dictionary<string, object> {
                ["authorization_endpoint"] = Rest.Path("https://login.microsoftonline.com", tenant, "oauth2", "v2.0", "authorize"),
                ["token_endpoint"] = Rest.Path("https://login.microsoftonline.com", tenant, "oauth2", "v2.0", "token"),
                ["issuer"] = Rest.Path("https://login.microsoftonline.com", tenant, "v2.0")
            });
        if (request.Method == HttpMethod.Post && uri!.AbsolutePath.TrimStart('/') == Rest.Path(tenant, "oauth2", "v2.0", "token")) {
            TokenRequests++;
            string content = await request.Content!.ReadAsStringAsync(cancellationToken);
            Dictionary<string, string> form = content.Split('&').Select(item => item.Split('=', 2))
                .ToDictionary(item => Uri.UnescapeDataString(item[0].Replace('+', ' ')), item => Uri.UnescapeDataString(item[1].Replace('+', ' ')));
            TokenScopeMatches = form.GetValueOrDefault("scope") == "https://api.botframework.com/.default";
            CredentialMatches = form.GetValueOrDefault("client_id") == TeamsWebhookFixture.ClientId &&
                form.GetValueOrDefault("client_secret") == "synthetic-fixture-secret" && form.GetValueOrDefault("grant_type") == "client_credentials";
        }
        throw new InvalidOperationException("The synthetic authority boundary was reached.");
    }

    static HttpResponseMessage Metadata(Dictionary<string, object> fields) => new(HttpStatusCode.OK) {
        Content = new StringContent(Json.WriteString(fields), Encoding.UTF8, "application/json")
    };
}
