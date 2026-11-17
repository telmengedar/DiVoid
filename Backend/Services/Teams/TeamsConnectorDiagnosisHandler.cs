using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using Backend.Models.Messages;
using Microsoft.Teams.Core.Http;

namespace Backend.Services.Teams;

/// <summary>Observes one cloned native Connector read without retaining authentication or challenge values.</summary>
sealed class TeamsConnectorDiagnosisHandler(HttpClient client, Uri expectedRoute, string botAppId, MessageReplyDiagnosis result) : HttpMessageHandler {
    const string Resource = "https://api.botframework.com";
    const string CredentialTenant = "d6d49420-f39b-4df7-a1dc-d59a935871db";
    int requests;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        result.OutboundRouteMatchesRetainedOrigin = request.Method == HttpMethod.Get && request.Content == null &&
            request.RequestUri == expectedRoute;
        if (Interlocked.Increment(ref requests) != 1 || result.OutboundRouteMatchesRetainedOrigin != true)
            throw new InvalidOperationException("The diagnosis transport permits one retained-origin GET.");
        using HttpRequestMessage copy = new(request.Method, request.RequestUri) {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };
        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (KeyValuePair<string, object> option in request.Options)
            copy.Options.Set(new HttpRequestOptionsKey<object>(option.Key), option.Value);
        try {
            HttpResponseMessage response = await client.SendAsync(copy, cancellationToken);
            ObserveAuthorization(copy);
            ObserveChallenge(response);
            return response;
        } finally {
            copy.Headers.Authorization = null;
        }
    }

    void ObserveAuthorization(HttpRequestMessage request) {
        result.RequestContextBotMatchesConfiguredBot = request.Options.TryGetValue(
            new HttpRequestOptionsKey<object>(BotRequestContext.BotAppIdKey), out object contextBot) && contextBot is string id &&
            Guid.TryParse(id, out Guid parsedContextBot) && parsedContextBot.ToString("D") == botAppId;
        string[] headers = request.Headers.TryGetValues("Authorization", out IEnumerable<string> values) ? values.ToArray() : [];
        AuthenticationHeaderValue.TryParse(headers.Length == 1 ? headers[0] : null, out AuthenticationHeaderValue header);
        string token = header?.Parameter;
        result.HeaderSingleBearer = header?.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) == true &&
            !string.IsNullOrEmpty(token) && !token.Any(char.IsWhiteSpace) &&
            headers.Length == 1;
        if (string.IsNullOrEmpty(token))
            return;
        JwtSecurityTokenHandler parser = new();
        result.JwtReadable = parser.CanReadToken(token);
        if (result.JwtReadable != true)
            return;
        try {
            JwtSecurityToken jwt = parser.ReadJwtToken(token);
            string[] audiences = jwt.Audiences.ToArray();
            string[] parties = jwt.Claims.Where(claim => claim.Type is "appid" or "azp").Select(claim => claim.Value).ToArray();
            long? notBefore = jwt.Payload.NotBefore;
            long? expires = jwt.Payload.Expiration;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            result.AudienceMatchesConnectorResource = audiences.Length == 0 ? null : audiences.Length == 1 && audiences[0] == Resource;
            result.AuthorizedPartyMatchesConfiguredBot = parties.Length == 0 ? null : parties.All(party =>
                Guid.TryParse(party, out Guid parsed) && parsed.ToString("D") == botAppId);
            result.IssuerMatchesKnownBotFrameworkIssuer = string.IsNullOrEmpty(jwt.Issuer) ? null : jwt.Issuer is
                "https://sts.windows.net/d6d49420-f39b-4df7-a1dc-d59a935871db/" or
                "https://login.microsoftonline.com/d6d49420-f39b-4df7-a1dc-d59a935871db/v2.0";
            result.ValidityWindowMatchesNow = notBefore.HasValue && expires.HasValue ? notBefore <= now && expires > now : null;
        } catch (Exception) {
            result.JwtReadable = false;
            result.AudienceMatchesConnectorResource = null;
            result.AuthorizedPartyMatchesConfiguredBot = null;
            result.IssuerMatchesKnownBotFrameworkIssuer = null;
            result.ValidityWindowMatchesNow = null;
        }
    }

    void ObserveChallenge(HttpResponseMessage response) {
        result.AuthenticationChallenge = "absent";
        result.AuthenticationChallengeError = "absent";
        result.AuthenticationChallengeAuthority = "absent";
        AuthenticationHeaderValue[] challenges;
        try {
            challenges = response.Headers.WwwAuthenticate.ToArray();
        } catch (FormatException) {
            result.AuthenticationChallenge = "unrecognized";
            return;
        }
        if (challenges.Length == 0) {
            if (response.Headers.Contains("WWW-Authenticate"))
                result.AuthenticationChallenge = "unrecognized";
            return;
        }
        AuthenticationHeaderValue[] bearer = challenges.Where(challenge =>
            challenge.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (bearer.Length != 1) {
            result.AuthenticationChallenge = bearer.Length == 0 ? "other" : "multiple_bearer";
            return;
        }
        result.AuthenticationChallenge = "bearer";
        if (!TryParameters(bearer[0].Parameter, out Dictionary<string, string> parameters)) {
            result.AuthenticationChallengeError = "unrecognized";
            result.AuthenticationChallengeAuthority = "unrecognized";
            return;
        }
        if (parameters.TryGetValue("error", out string error))
            result.AuthenticationChallengeError = error is "invalid_token" or "invalid_request" or "insufficient_scope"
                ? error : "unrecognized";
        string[] authorities = new[] { "authorization_uri", "issuer" }.Where(parameters.ContainsKey)
            .Select(key => ClassifyAuthority(parameters[key])).Distinct().ToArray();
        if (authorities.Length > 0)
            result.AuthenticationChallengeAuthority = authorities.Length == 1 ? authorities[0] : "ambiguous";
        string[] resources = new[] { "resource", "resource_id" }.Where(parameters.ContainsKey)
            .Select(key => parameters[key]).ToArray();
        if (resources.Length > 0)
            result.AuthenticationChallengeResourceMatchesConnector = resources.All(resource => resource == Resource);
    }

    static bool TryParameters(string value, out Dictionary<string, string> parameters) {
        parameters = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
            return true;
        if (value.Length > 8192)
            return false;
        bool quoted = false;
        bool escaped = false;
        int start = 0;
        for (int index = 0; index <= value.Length; index++) {
            if (index < value.Length) {
                char character = value[index];
                if (escaped) {
                    escaped = false;
                    continue;
                }
                if (quoted && character == '\\') {
                    escaped = true;
                    continue;
                }
                if (character == '"')
                    quoted = !quoted;
                if (character != ',' || quoted)
                    continue;
            }
            if (quoted || escaped || !NameValueHeaderValue.TryParse(value[start..index].Trim(), out NameValueHeaderValue parameter) ||
                parameter.Value == null || !parameters.TryAdd(parameter.Name,
                    parameter.Value.StartsWith('"') ? parameter.Value[1..^1] : parameter.Value))
                return false;
            start = index + 1;
        }
        return true;
    }

    static string ClassifyAuthority(string value) {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri authority) || authority.Scheme != Uri.UriSchemeHttps ||
            authority.Port != 443 || !string.IsNullOrEmpty(authority.UserInfo) ||
            !string.IsNullOrEmpty(authority.Query) || !string.IsNullOrEmpty(authority.Fragment))
            return "unrecognized";
        string[] path = authority.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (path.Length == 0)
            return "unrecognized";
        if (authority.Host == "sts.windows.net")
            return path.Length == 1 && Guid.TryParse(path[0], out Guid issuerTenant)
                ? issuerTenant.ToString("D") == CredentialTenant ? "public_botframework" : "other_tenant"
                : "unrecognized";
        if (authority.Host != "login.microsoftonline.com")
            return "unrecognized";
        if (path.Length != 1 && !(path.Length == 2 && path[1] == "v2.0") &&
            !(path.Length == 3 && path[1] == "oauth2" && path[2] == "authorize") &&
            !(path.Length == 4 && path[1] == "oauth2" && path[2] == "v2.0" && path[3] == "authorize"))
            return "unrecognized";
        if (path[0] == "botframework.com" || Guid.TryParse(path[0], out Guid tenant) && tenant.ToString("D") == CredentialTenant)
            return "public_botframework";
        return Guid.TryParse(path[0], out _) ? "other_tenant" : "unrecognized";
    }
}
