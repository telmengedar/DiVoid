using Backend.Models.Attributes;
using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Ocelot.Entities.Attributes;

namespace Backend.Models.Users;

/// <summary>
/// user of system
/// </summary>
[AllowPatch]
[Table("divoid_user")]
public class User {

    /// <summary>
    /// id of user
    /// </summary>
    [PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    /// <summary>
    /// name of user
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// optional email address for out-of-band contact
    /// </summary>
    [Index("email")]
    [AllowPatch]
    public string Email { get; set; }

    /// <summary>
    /// whether the user is enabled; disabling blocks all of the user's keys
    /// </summary>
    [AllowPatch]
    public bool Enabled { get; set; }

    /// <summary>
    /// timestamp when the user was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// JSON-encoded array of permissions for Keycloak-authenticated requests
    /// (same encoding as <see cref="Backend.Models.Auth.ApiKey.Permissions"/>).
    /// Null/empty for users who only authenticate via API keys.
    /// Allowed values: admin, write, read.
    /// </summary>
    [AllowPatch]
    [JsonColumn]
    public string Permissions { get; set; }

    /// <summary>
    /// Optional id of the user's "home node" — the graph anchor used by the
    /// frontend to filter selectors (e.g. orgs / projects pill rows) to the
    /// user's working set. Pure frontend hint; backend does NOT enforce
    /// visibility based on this value.
    /// </summary>
    [AllowPatch]
    public long? HomeNodeId { get; set; }
}
