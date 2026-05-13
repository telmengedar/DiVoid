namespace Backend.Models.Nodes;

/// <summary>
/// node
/// </summary>
public class NodeDetails
{
    /// <summary>
    /// id of node
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// id of type
    /// </summary>
    public string Type { get; set; }

    /// <summary>
    /// name of node
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// lifecycle status of the node (e.g. "open", "closed", "in-progress")
    /// </summary>
    public string Status { get; set; }

    /// <summary>
    /// MIME content type of the node's blob content, if any (e.g. "text/markdown", "application/json", "image/png").
    /// Absent when the node has no content.
    /// </summary>
    public string ContentType { get; set; }

    /// <summary>
    /// cosine similarity score in the range [0, 1] (1.0 = identical direction).
    /// present only when the listing was triggered by a <c>?query=</c> semantic search.
    /// null — and omitted from the JSON response — when no <c>query</c> was supplied.
    /// </summary>
    public float? Similarity { get; set; }

    /// <summary>
    /// X position in the shared workspace canvas (world units).
    /// null when the field was not requested via <c>?fields=</c>.
    /// </summary>
    public double? X { get; set; }

    /// <summary>
    /// Y position in the shared workspace canvas (world units).
    /// null when the field was not requested via <c>?fields=</c>.
    /// </summary>
    public double? Y { get; set; }
}
