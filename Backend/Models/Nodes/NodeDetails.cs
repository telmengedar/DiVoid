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
}
