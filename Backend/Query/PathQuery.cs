namespace Backend.Query;

/// <summary>
/// the parsed representation of a full path expression.
/// In v1 this is always a linear chain.  The envelope exists so that deferred
/// OrNode / AndNode peers can be added without changing the root type.
/// </summary>
public class PathQuery
{
    /// <summary>
    /// hops in traversal order, left to right
    /// </summary>
    public HopSegment[] Hops { get; init; }
}
