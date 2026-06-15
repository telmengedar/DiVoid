using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Ocelot.Entities.Attributes;

namespace Backend.Models.Nodes;

/// <summary>
/// node
/// </summary>
public class Node
{

    /// <summary>
    /// id of node
    /// </summary>
    [PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    /// <summary>
    /// id of type
    /// </summary>
    [Index("type")]
    [Index("node")]
    [Index("nodestatus")]
    [Index("typeseverity")]
    public long TypeId { get; set; }

    /// <summary>
    /// name of node
    /// </summary>
    [AllowPatch]
    [Index("node")]
    public string Name { get; set; }

    /// <summary>
    /// type of node content
    /// </summary>
    public string ContentType { get; set; }

    /// <summary>
    /// node content
    /// </summary>
    public byte[] Content { get; set; }

    /// <summary>
    /// content embedding (only for text / markdown nodes)
    /// </summary>
    [Size(3072)]
    public float[] Embedding { get; set; }

    /// <summary>
    /// lifecycle status of the node (e.g. "open", "closed", "in-progress")
    /// </summary>
    [AllowPatch]
    [Index("status")]
    [Index("nodestatus")]
    public string Status { get; set; }

    /// <summary>
    /// abstract numeric priority/importance signal interpreted per node type by application clients; null means unset.
    /// </summary>
    [AllowPatch]
    [Index("severity")]
    [Index("typeseverity")]
    public int? Severity { get; set; }

    /// <summary>
    /// X position of the node in the shared workspace canvas (world units).
    /// default 0.0; patchable via <c>PATCH /api/nodes/{id}</c> with <c>replace /X</c>.
    /// </summary>
    [AllowPatch]
    [Index("position")]
    [DefaultValue(0.0)]
    public double X { get; set; }

    /// <summary>
    /// Y position of the node in the shared workspace canvas (world units).
    /// default 0.0; patchable via <c>PATCH /api/nodes/{id}</c> with <c>replace /Y</c>.
    /// </summary>
    [AllowPatch]
    [Index("position")]
    [DefaultValue(0.0)]
    public double Y { get; set; }

    /// <summary>
    /// DiVoid user-id of the node's creator. set once on insert from the authenticated caller.
    /// sentinel 0 for rows that pre-date this feature. admin override applies regardless.
    /// </summary>
    [AllowPatch]
    [Index("owner")]
    [DefaultValue(0L)]
    public long OwnerId { get; set; }

    /// <summary>
    /// access flags controlling what non-owner non-admin callers may do with this node.
    /// owner and admin always override. defaults to <see cref="NodeAccess.Read"/> | <see cref="NodeAccess.Write"/>
    /// (fully public) to preserve the pre-access-layer posture of existing rows.
    /// </summary>
    [AllowPatch]
    [Index("access")]
    [DefaultValue((int)(NodeAccess.Read | NodeAccess.Write))]
    public NodeAccess Access { get; set; }

    /// <summary>
    /// id of the owning <see cref="Backend.Models.Organizations.Organization"/>; non-null,
    /// defaults to <see cref="Backend.Models.Organizations.Organization.BootstrapOrgIdConst"/>, admin-only PATCH.
    /// </summary>
    [AllowPatch]
    [Index("organization")]
    [DefaultValue(Backend.Models.Organizations.Organization.BootstrapOrgIdConst)]
    public long OrganizationId { get; set; }

    /// <summary>
    /// UTC timestamp when this node was created.
    /// </summary>
    [Index("created")]
    [DefaultValue("0001-01-01 00:00:00")]
    public DateTime Created { get; set; }

    /// <summary>
    /// UTC timestamp of the last metadata or content modification to this node.
    /// </summary>
    [Index("lastupdate")]
    [DefaultValue("0001-01-01 00:00:00")]
    public DateTime LastUpdate { get; set; }
}
