namespace Backend.Models.Nodes;

/// <summary>
/// one row in the live type-catalog response.
/// represents a node type that is currently in use (count &gt;= 1).
/// </summary>
public class TypeListItem
{

    /// <summary>
    /// primary-key id of the <see cref="NodeType"/> row
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// type name as used across the rest of the API (e.g. "task", "documentation", "product")
    /// </summary>
    public string Type { get; set; }

    /// <summary>
    /// number of <see cref="Node"/> rows currently using this type; always &gt;= 1
    /// </summary>
    public long Count { get; set; }
}
