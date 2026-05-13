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
}
