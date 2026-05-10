using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Fields;
using Pooshit.Ocelot.Tokens;

namespace Backend.Models.Nodes;

/// <summary>
/// mapper for node data
/// </summary>
public class NodeMapper : FieldMapper<NodeDetails, Node>
{

    /// <summary>
    /// creates a new <see cref="NodeMapper"/>
    /// </summary>
    public NodeMapper()
    : base(Mappings()) { }

    /// <inheritdoc />
    public override string[] DefaultListFields => ["id", "type", "name", "status", "contentType"];

    static IEnumerable<FieldMapping<NodeDetails>> Mappings()
    {
        yield return new FieldMapping<NodeDetails, long>("id",
                                                        DB.Property<Node>(n => n.Id, "node"),
                                                        (n, v) => n.Id = v);
        yield return new FieldMapping<NodeDetails, string>("type",
                                                        DB.Property<NodeType>(n => n.Type, "type"),
                                                        (n, v) => n.Type = v);
        yield return new FieldMapping<NodeDetails, string>("name",
                                                        DB.Property<Node>(n => n.Name, "node"),
                                                        (n, v) => n.Name = v);
        yield return new FieldMapping<NodeDetails, string>("status",
                                                        DB.Property<Node>(n => n.Status, "node"),
                                                        (n, v) => n.Status = v);
        yield return new FieldMapping<NodeDetails, string>("contentType",
                                                        DB.Property<Node>(n => n.ContentType, "node"),
                                                        (n, v) => n.ContentType = v);
    }

    /// <inheritdoc />
    public override LoadOperation<Node> CreateOperation(IEntityManager database, params IDBField[] fields)
    {
        return database.Load<Node>(fields)
                        .Alias("node")
                        .Join<NodeType>((n, t) => n.TypeId == t.Id, "type");
    }
}
