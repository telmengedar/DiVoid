namespace Backend.Query;

/// <summary>
/// a single predicate within a path segment, e.g. <c>type:task</c> or <c>status:open|new</c>
/// </summary>
public class HopPredicate
{
    /// <summary>
    /// the key name (id, type, name, status)
    /// </summary>
    public string Key { get; init; }

    /// <summary>
    /// one or more values (OR semantics within the key)
    /// </summary>
    public string[] Values { get; init; }

    /// <summary>
    /// reserved for deferred !negation support; always false in v1
    /// </summary>
    public bool Negated { get; init; }
}

/// <summary>
/// one hop in a path expression, corresponding to one bracketed segment.
/// Empty <see cref="Predicates"/> means "any node" (the <c>[]</c> wildcard).
/// </summary>
public class HopSegment
{
    /// <summary>
    /// predicates joined by AND (comma-separated within the bracket)
    /// </summary>
    public HopPredicate[] Predicates { get; init; }

    /// <summary>
    /// reserved for deferred !segment-negation support; always false in v1
    /// </summary>
    public bool Negated { get; init; }
}

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
