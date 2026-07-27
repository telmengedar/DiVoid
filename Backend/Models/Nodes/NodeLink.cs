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

    /// <summary>
    /// direction semantics of this edge. defaults to <see cref="Nodes.LinkType.None"/> (undirected,
    /// today's behavior) so existing rows read back unchanged after this column is added.
    /// not queried/filtered — no index (read-metadata, not a query filter).
    /// </summary>
    [DefaultValue((int)LinkType.None)]
    public LinkType LinkType { get; set; }

    /// <summary>
    /// optional free-text label carried on the edge, interpreted in the source→target direction
    /// (e.g. "subtask" reads as source --subtask--> target). null means no context (today's behavior).
    /// not queried/filtered — no index.
    /// </summary>
    public string Context { get; set; }
}
