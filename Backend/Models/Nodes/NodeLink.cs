using Pooshit.Ocelot.Entities.Attributes;

namespace Backend.Models.Nodes;

/// <summary>
/// link between nodes
/// </summary>
public class NodeLink
{

    /// <summary>
    /// id of source node
    /// </summary>
    [Index("source")]
    public long SourceId { get; set; }

    /// <summary>
    /// id of target node
    /// </summary>
    [Index("target")]
    public long TargetId { get; set; }
}
