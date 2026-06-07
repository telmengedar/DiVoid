using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Ocelot.Entities.Attributes;

namespace Backend.Models.Organizations;

/// <summary>
/// organization that owns nodes and groups users into a membership boundary.
/// see <c>docs/architecture/organizations.md</c> for the visibility model.
/// </summary>
[AllowPatch]
public class Organization
{

    /// <summary>
    /// stable id of the bootstrap "DiVoid" organization, seeded on first boot by
    /// <see cref="Backend.Init.DatabaseModelService"/>. used as the column default for
    /// <see cref="Backend.Models.Nodes.Node.OrganizationId"/> so existing nodes back-fill cleanly.
    /// </summary>
    public const long BootstrapOrgIdConst = 1;

    /// <summary>
    /// id of organization
    /// </summary>
    [PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    /// <summary>
    /// display name of the organization; not enforced unique.
    /// </summary>
    [AllowPatch]
    [Index("name")]
    public string Name { get; set; }

    /// <summary>
    /// DiVoid user-id of the organization's creator; admin override applies regardless.
    /// sentinel 0 for the bootstrap row and for rows seeded outside the API.
    /// </summary>
    [AllowPatch]
    [DefaultValue(0L)]
    public long OwnerId { get; set; }

    /// <summary>
    /// UTC timestamp when the organization row was created.
    /// </summary>
    [DefaultValue("0001-01-01 00:00:00")]
    public DateTime Created { get; set; }

    /// <summary>
    /// UTC timestamp of the last metadata change to the organization row.
    /// </summary>
    [DefaultValue("0001-01-01 00:00:00")]
    public DateTime LastUpdate { get; set; }
}
