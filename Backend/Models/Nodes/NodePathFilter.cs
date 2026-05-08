namespace Backend.Models.Nodes;

/// <summary>
/// filter for path-query traversal on <c>GET /api/nodes?path=...</c>.
/// Extends <see cref="NodeFilter"/> so that paging, sort, fields, and all standard
/// node filters are inherited.  The <c>path</c> field activates graph-path mode;
/// all other fields apply to the <b>terminal hop only</b>.
/// </summary>
public class NodePathFilter : NodeFilter
{
    /// <summary>
    /// the raw path expression, e.g.
    /// <c>[type:organization,name:Pooshit]/[type:project,name:DiVoid]/[type:task,status:open]</c>.
    /// When non-null the controller routes to path-query mode instead of the standard list.
    /// </summary>
    public string Path { get; set; }
}
