using Pooshit.Ocelot.Entities.Attributes;

namespace Backend.Models.Nodes;

/// <summary>
/// node
/// </summary>
public class Node
{

    /// <summary>
    /// id of node
    /// </summary>
    [PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    /// <summary>
    /// id of type
    /// </summary>
    [Index("type")]
    [Index("node")]
    public long TypeId { get; set; }

    /// <summary>
    /// name of node
    /// </summary>
    [Index("node")]
    public string Name { get; set; }

    /// <summary>
    /// type of node content
    /// </summary>
    public string ContentType { get; set; }

    /// <summary>
    /// node content
    /// </summary>
    public byte[] Content { get; set; }

    /// <summary>
	/// content embedding (only for text / markdown nodes)
	/// </summary>
	[Size(3072)]
	public float[] Embedding { get; set; }
}
