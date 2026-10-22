using Pooshit.Ocelot.Entities.Attributes;

namespace Backend.Models.Nodes;

/// <summary>subscription to changes of a node</summary>
public class NodeHook {
    /// <summary>watched node id</summary>
    [Unique("subscription")]
    public long NodeId { get; set; }

    /// <summary>recipient user id</summary>
    [Unique("subscription")]
    public long UserId { get; set; }

    /// <summary>message routing tag</summary>
    [Unique("subscription"), Size(128)]
    public string Tag { get; set; }
}
