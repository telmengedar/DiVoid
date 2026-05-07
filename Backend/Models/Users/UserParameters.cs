namespace Backend.Models.Users;

/// <summary>
/// parameters used to create a new user
/// </summary>
public class UserParameters {

    /// <summary>
    /// name of user
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// optional email address
    /// </summary>
    public string Email { get; set; }
}
