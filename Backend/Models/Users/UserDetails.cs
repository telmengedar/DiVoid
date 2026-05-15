namespace Backend.Models.Users;

/// <summary>
/// user information returned by the API
/// </summary>
public class UserDetails {

    /// <summary>
    /// id of user
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// name of user
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// optional email address for out-of-band contact
    /// </summary>
    public string Email { get; set; }

    /// <summary>
    /// whether the user is enabled
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// timestamp when the user was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// permissions for Keycloak-authenticated requests (empty array when none are set).
    /// Allowed values: admin, write, read.
    /// </summary>
    public string[] Permissions { get; set; }

    /// <summary>
    /// optional id of the user's "home node" — the graph anchor used by the
    /// frontend to filter selectors to the user's working set.
    /// Null when not set.
    /// </summary>
    public long? HomeNodeId { get; set; }
}
