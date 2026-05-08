using Pooshit.AspNetCore.Services.Data;

namespace Backend.Models.Nodes;

/// <summary>
/// filter for path-query traversal on <c>GET /api/nodes?path=...</c>.
/// Carries paging / sort / fields from <see cref="ListFilter"/> which all apply
/// to the <b>terminal hop only</b>.
/// </summary>
public class NodePathFilter : ListFilter
{
    /// <summary>
    /// the raw path expression, e.g.
    /// <c>[type:organization,name:Pooshit]/[type:project,name:DiVoid]/[type:task,status:open]</c>
    /// </summary>
    public string Path { get; set; }

    /// <summary>
    /// when <see langword="true"/>, the response omits the <c>total</c> field and skips
    /// the COUNT query.  Applies to both the existing list endpoint and the path-query mode.
    /// </summary>
    public bool NoTotal { get; set; }
}
