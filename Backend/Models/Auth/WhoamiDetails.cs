namespace Backend.Models.Auth;

/// <summary>
/// identity and permissions of the authenticated principal
/// </summary>
public class WhoamiDetails {

    /// <summary>
    /// DiVoid user id of the authenticated principal
    /// </summary>
    public long UserId { get; set; }

    /// <summary>
    /// display name of the user
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// email address of the user (null if not set)
    /// </summary>
    public string Email { get; set; }

    /// <summary>
    /// permissions held by the principal.
    /// For JWT-authenticated requests this is the owning user's permission set.
    /// For API-key-authenticated requests this is the key's own permission set,
    /// which may differ from the owning user's permissions.
    /// </summary>
    public string[] Permissions { get; set; }
}
