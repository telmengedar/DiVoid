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
}
