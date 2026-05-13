namespace Backend.Models.Nodes;

/// <summary>
/// a directed link pair returned by the adjacency endpoint.
/// the edge is undirected in the graph; <see cref="SourceId"/> and <see cref="TargetId"/>
/// reflect the order stored in <see cref="NodeLink"/>.
/// </summary>
public class LinkAdjacency
{

    /// <summary>
    /// id of the source node of this link
    /// </summary>
    public long SourceId { get; set; }

    /// <summary>
    /// id of the target node of this link
    /// </summary>
    public long TargetId { get; set; }
}
