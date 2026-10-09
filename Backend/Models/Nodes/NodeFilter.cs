using mamgo.services.Binding;
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
    /// when true, only return nodes with no type set (null or empty string)
    /// </summary>
    public bool NoType { get; set; }

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
    /// severity values to filter for; matches nodes whose severity is in this list
    /// </summary>
    public int[] Severity { get; set; }

    /// <summary>
    /// inclusive lower bound on <see cref="Node.Severity"/>: only nodes with severity &gt;= this value are returned
    /// </summary>
    public int? SeverityMin { get; set; }

    /// <summary>
    /// inclusive upper bound on <see cref="Node.Severity"/>: only nodes with severity &lt;= this value are returned
    /// </summary>
    public int? SeverityMax { get; set; }

    /// <summary>
    /// when true, only return nodes with no severity set (null)
    /// </summary>
    public bool NoSeverity { get; set; }

    /// <summary>
    /// refinement values to filter for; matches nodes whose refinement is in this list.
    /// OR-composed with <see cref="NoRefinement"/> when both are present.
    /// </summary>
    public string[] Refinement { get; set; }

    /// <summary>
    /// when true, only return nodes with no refinement set (null or empty string).
    /// OR-composed with <see cref="Refinement"/> when both are present.
    /// </summary>
    public bool NoRefinement { get; set; }

    /// <summary>
    /// root-node ids to filter for; returns only nodes grouped under one of the listed root nodes.
    /// OR-composed with <see cref="NoRootNodeId"/> when both are present.
    /// </summary>
    public long[] RootNodeId { get; set; }

    /// <summary>
    /// when true, only return nodes with no root node set (null, i.e. ungrouped).
    /// OR-composed with <see cref="RootNodeId"/> when both are present.
    /// </summary>
    public bool NoRootNodeId { get; set; }

    /// <summary>
    /// minimum similarity floor (0–1) for semantic search results.
    /// only meaningful when <see cref="Pooshit.AspNetCore.Services.Data.ListFilter.Query"/> or <see cref="Queries"/> is also supplied;
    /// supplying <c>minSimilarity</c> without <c>query</c> or <c>queries</c> returns HTTP 400.
    /// </summary>
    public float? MinSimilarity { get; set; }

    /// <summary>
    /// semantic queries ranked together by the mean of their per-query similarities;
    /// each repeated <c>queries</c> key is one query and a value containing commas stays one query
    /// </summary>
    [NoCommaSplit]
    public string[] Queries { get; set; }

    /// <summary>
    /// viewport bounding rectangle expressed as four comma-separated doubles:
    /// <c>xMin,yMin,xMax,yMax</c> (world units, inclusive bounds).
    /// when present, only nodes whose <c>X</c> and <c>Y</c> fall inside the rectangle are returned.
    /// length must be exactly 4; xMin must be ≤ xMax and yMin must be ≤ yMax — otherwise HTTP 400.
    /// </summary>
    public double[] Bounds { get; set; }

    /// <summary>
    /// inclusive lower bound on <see cref="Node.Created"/>: only nodes created at or after this timestamp are returned.
    /// </summary>
    public DateTime? CreatedFrom { get; set; }

    /// <summary>
    /// exclusive upper bound on <see cref="Node.Created"/>: only nodes created before this timestamp are returned.
    /// </summary>
    public DateTime? CreatedTo { get; set; }

    /// <summary>
    /// inclusive lower bound on <see cref="Node.LastUpdate"/>: only nodes last updated at or after this timestamp are returned.
    /// </summary>
    public DateTime? UpdatedFrom { get; set; }

    /// <summary>
    /// exclusive upper bound on <see cref="Node.LastUpdate"/>: only nodes last updated before this timestamp are returned.
    /// </summary>
    public DateTime? UpdatedTo { get; set; }

    /// <summary>
    /// the semantic queries a search runs with: <see cref="Queries"/> when non-empty, otherwise the single query when not blank, otherwise none
    /// </summary>
    /// <returns>queries to rank by</returns>
    public string[] GetEffectiveQueries()
    {
        if (Queries?.Length > 0)
            return [.. Queries];

        return string.IsNullOrWhiteSpace(Query) ? [] : [Query];
    }
}
