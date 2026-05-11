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
    /// minimum cosine similarity floor (0–1) for semantic search results.
    /// only meaningful when <see cref="Pooshit.AspNetCore.Services.Data.ListFilter.Query"/> is also supplied;
    /// supplying <c>minSimilarity</c> without <c>query</c> returns HTTP 400.
    /// </summary>
    public float? MinSimilarity { get; set; }
}
