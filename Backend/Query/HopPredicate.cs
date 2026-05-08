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
