using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Pooshit.Json;

namespace Backend.tests.Fixtures;

/// <summary>Serves synthetic authoritative Connector discovery and endorsed signing keys.</summary>
public sealed class TeamsMetadataHandler : HttpMessageHandler {
    /// <summary>The currently published signing key.</summary>
    public RsaSecurityKey Key { get; set; }
    /// <summary>The published channel endorsements.</summary>
    public string[]? Endorsements { get; set; } = ["msteams"];
    /// <summary>The number of authoritative key reads.</summary>
    public int KeyRequests { get; private set; }

    /// <summary>Creates the synthetic metadata transport.</summary>
    public TeamsMetadataHandler(RsaSecurityKey key) {
        Key = key;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        string json;
        if (request.RequestUri!.AbsolutePath.EndsWith("openid-configuration")) {
            json = Json.WriteString(new Dictionary<string, object> {
                ["issuer"] = "https://api.botframework.com",
                ["jwks_uri"] = "https://login.botframework.com/v1/.well-known/keys"
            });
        } else if (request.RequestUri.AbsoluteUri == "https://login.botframework.com/v1/.well-known/keys") {
            KeyRequests++;
            RSAParameters parameters = Key.Rsa.ExportParameters(false);
            Dictionary<string, object> key = new() {
                ["kty"] = "RSA", ["use"] = "sig", ["alg"] = "RS256", ["kid"] = Key.KeyId,
                ["n"] = Base64UrlEncoder.Encode(parameters.Modulus),
                ["e"] = Base64UrlEncoder.Encode(parameters.Exponent)
            };
            if (Endorsements != null)
                key["endorsements"] = Endorsements;
            json = Json.WriteString(new Dictionary<string, object> { ["keys"] = new[] { key } });
        } else {
            throw new InvalidOperationException("Unexpected Connector metadata destination.");
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    }
}
