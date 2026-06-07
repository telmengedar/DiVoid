using Pooshit.Ocelot.Entities.Attributes;

namespace Backend.Models.Organizations;

/// <summary>
/// many-to-many membership row binding a <see cref="Backend.Models.Users.User"/> to an
/// <see cref="Organization"/>. set-shaped: presence of the row = membership.
/// </summary>
public class UserOrganization
{

    /// <summary>
    /// user that is a member of <see cref="OrganizationId"/>.
    /// composite-key half plus an indexed lookup for the per-user membership query.
    /// </summary>
    [Index("user")]
    [Index("user_org")]
    public long UserId { get; set; }

    /// <summary>
    /// organization that <see cref="UserId"/> is a member of.
    /// composite-key half plus an indexed lookup for the per-org listing query.
    /// </summary>
    [Index("organization")]
    [Index("user_org")]
    public long OrganizationId { get; set; }
}
