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
}
