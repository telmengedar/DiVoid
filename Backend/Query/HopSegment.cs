namespace Backend.Query;

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
