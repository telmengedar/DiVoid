using Pooshit.AspNetCore.Services.Data;

namespace Backend.Models.Nodes;

/// <summary>
/// filter for <see cref="Node"/> list operations
/// </summary>
public class NodeFilter : ListFilter
{

    /// <summary>
    /// id of node to filter for
    /// </summary>
    public long[] Id { get; set; }

    /// <summary>
    /// type of node to filter for
    /// </summary>
    public string[] Type { get; set; }

    /// <summary>
    /// name of node to filter for
    /// </summary>
    public string[] Name { get; set; }

    /// <summary>
    /// nodes node needs to be linked to
    /// </summary>
    public long[] LinkedTo { get; set; }

    /// <summary>
    /// status values to filter for
    /// </summary>
    public string[] Status { get; set; }

    /// <summary>
    /// when true, only return nodes with no status set (null or empty string)
    /// </summary>
    public bool NoStatus { get; set; }

    /// <summary>
    /// when <see langword="true"/>, omit the <c>total</c> field from the response and skip
    /// the COUNT query.  Useful for large graphs where the count is not needed.
    /// </summary>
    public bool NoTotal { get; set; }
}
