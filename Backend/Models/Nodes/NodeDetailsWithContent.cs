namespace Backend.Models.Nodes;

/// <summary>
/// node
/// </summary>
public class NodeDetailsWithContent
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
    /// type of node content
    /// </summary>
    public string ContentType { get; set; }


}
