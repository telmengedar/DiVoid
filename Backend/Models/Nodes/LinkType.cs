namespace Backend.Models.Nodes;

/// <summary>
/// direction semantics carried on a <see cref="NodeLink"/> edge.
/// direction is read-metadata for consumers traversing the graph; it does not affect
/// traversal itself (<c>linkedto</c> stays both-directions regardless of this value).
/// </summary>
public enum LinkType
{
    /// <summary>undirected — order of <see cref="NodeLink.SourceId"/>/<see cref="NodeLink.TargetId"/> carries no meaning (today's behavior)</summary>
    None = 0,

    /// <summary>one arrow, <see cref="NodeLink.SourceId"/> → <see cref="NodeLink.TargetId"/></summary>
    Unidirectional = 1,

    /// <summary>both ends directed, each direction meaningful</summary>
    Bidirectional = 2
}
