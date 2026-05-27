using System.Runtime.Serialization;

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
    /// encoded content body for this node, present only when <c>content</c> is included in
    /// <c>?fields=</c>.  text content (per <see cref="Backend.Services.Embeddings.TextContentTypePredicate.IsText"/>)
    /// is returned as a UTF-8 JSON string; binary content is returned as a base64 string.
    /// absent when the node has no content or when <c>content</c> was not requested.
    /// </summary>
    public string Content { get; set; }

    /// <summary>
    /// transient raw bytes populated by the <c>content</c> field-mapping in
    /// <see cref="NodeMapper"/>.  never serialised to JSON.
    /// the post-process callback in <see cref="NodeMapper"/> uses this alongside
    /// <see cref="ContentType"/> to encode the inline content into <see cref="Content"/>.
    /// </summary>
    [IgnoreDataMember]
    public byte[] RawContent { get; set; }

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

    /// <summary>
    /// ids of nodes adjacent to this node.
    /// on write (<c>POST /api/nodes</c>): ids of nodes to link at creation time, also
    /// drives auto-positioning when X and Y are unset.
    /// on read (<c>GET /api/nodes?fields=links</c>): opt-in inline neighbor ids.
    /// absent from the response when <c>links</c> is not in <c>?fields=</c>.
    /// </summary>
    public long[] Links { get; set; }
}
