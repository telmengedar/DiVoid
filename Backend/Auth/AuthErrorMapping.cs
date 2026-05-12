using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Backend.Auth;

/// <summary>
/// shared helper that serialises auth error bodies and maps failure reasons
/// to human-readable detail strings
/// </summary>
public static class AuthErrorMapping {
    static readonly JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// serialises a <c>{ status, title, detail }</c> JSON error body
    /// </summary>
    public static string SerializeError(int status, string title, string detail) =>
        JsonSerializer.Serialize(new { status, title, detail }, serializerOptions);

    /// <summary>
    /// maps a JWT validation exception to a human-readable detail string
    /// </summary>
    public static string MapJwtFailureToDetail(Exception ex) => ex switch {
        SecurityTokenExpiredException             => "JWT has expired",
        SecurityTokenInvalidAudienceException     => "JWT audience is not accepted by this service",
        SecurityTokenInvalidIssuerException       => "JWT issuer is not accepted by this service",
        SecurityTokenSignatureKeyNotFoundException => "JWT signature could not be verified",
        SecurityTokenInvalidSignatureException    => "JWT signature could not be verified",
        _                                         => "JWT could not be parsed"
    };

    /// <summary>
    /// maps an internal API-key failure reason string to a human-readable detail
    /// </summary>
    public static string MapApiKeyFailureToDetail(string reason) => reason switch {
        "disabled_key"  => "API key is disabled",
        "expired"       => "API key has expired",
        "disabled_user" => "DiVoid account is disabled",
        "invalid_key"   => "API key not recognised",
        _               => "API key not recognised"
    };
}
