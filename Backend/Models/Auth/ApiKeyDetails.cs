using System.Text.Json.Serialization;

namespace Backend.Models.Auth;

/// <summary>
/// full api key information
/// </summary>
public class ApiKeyDetails {

    /// <summary>
    /// id of key row
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// user for which key is valid
    /// </summary>
    public long? UserId { get; set; }

    /// <summary>
    /// short plaintext identifier (prefix of the full key string)
    /// </summary>
    public string KeyId { get; set; }

    /// <summary>
    /// permissions included in key
    /// </summary>
    public string[] Permissions { get; set; }

    /// <summary>
    /// whether the key is currently enabled
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// timestamp when the key was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// timestamp of last successful authentication
    /// </summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// optional expiry — null means no expiry
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// full key string returned exactly once on creation; null on all subsequent reads
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string PlaintextKey { get; set; }
}
