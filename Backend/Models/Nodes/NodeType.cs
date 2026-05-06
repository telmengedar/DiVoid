using System;
using Pooshit.Ocelot.Entities.Attributes;

namespace Backend.Models.Nodes;

/// <summary>
/// type of node
/// </summary>
public class NodeType
{
    /// <summary>
    /// id of node type
    /// </summary>
    [PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    /// <summary>
    /// type of node
    /// </summary>
    [Index("name")]
    public string Type { get; set; }
}
