using Pooshit.Ocelot.Entities.Attributes;

namespace Backend.Services.Users;

/// <summary>
/// user of system
/// </summary>
public class User
{

    /// <summary>
    /// id of user
    /// </summary>
    [PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    /// <summary>
    /// name of user
    /// </summary>
    public string Name { get; set; }
}
