namespace Backend.Models.Nodes;

/// <summary>
/// a single graph edge incident to a node, carrying its true stored source/target
/// orientation plus direction semantics and free-text context. populated only when
/// <c>linkDetails</c> is requested via <c>?fields=</c> (see <see cref="NodeDetails.LinkDetails"/>).
/// </summary>
public class LinkDetail
{
    /// <summary>
    /// id of the edge's source node, as stored on <see cref="NodeLink.SourceId"/>.
    /// </summary>
    public long SourceId { get; set; }

    /// <summary>
    /// id of the edge's target node, as stored on <see cref="NodeLink.TargetId"/>.
    /// </summary>
    public long TargetId { get; set; }

    /// <summary>
    /// direction semantics of this edge.
    /// </summary>
    public LinkType LinkType { get; set; }

    /// <summary>
    /// optional free-text label carried on the edge, interpreted source→target; null when unset.
    /// </summary>
    public string Context { get; set; }
}
