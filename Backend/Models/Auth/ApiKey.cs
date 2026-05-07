using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Ocelot.Entities.Attributes;

namespace Backend.Models.Auth;

/// <summary>
/// key used to access api from external systems
/// </summary>
[AllowPatch]
public class ApiKey {

    /// <summary>
    /// id of key
    /// </summary>
    [PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    /// <summary>
    /// user for which key is valid
    /// </summary>
    [AllowPatch]
    public long UserId { get; set; }

    /// <summary>
    /// short plaintext identifier used to look up the row (prefix of the full key)
    /// </summary>
    [Index("keyid")]
    public string KeyId { get; set; }

    /// <summary>
    /// HMAC-SHA-256(pepper, full_key_string) — 32 bytes
    /// </summary>
    public byte[] KeyHash { get; set; }

    /// <summary>
    /// permissions included in key
    /// </summary>
    [AllowPatch]
    public string Permissions { get; set; }

    /// <summary>
    /// whether the key is enabled and may authenticate requests
    /// </summary>
    [AllowPatch]
    public bool Enabled { get; set; }

    /// <summary>
    /// timestamp when the key was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// timestamp of last successful authentication (updated asynchronously)
    /// </summary>
    [AllowPatch]
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// optional expiry — null means no expiry
    /// </summary>
    [AllowPatch]
    public DateTime? ExpiresAt { get; set; }
}
