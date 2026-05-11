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
}
