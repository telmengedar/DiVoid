using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Fields;
using Pooshit.Ocelot.Tokens;

namespace Backend.Models.Nodes;

/// <summary>
/// mapper for the types-catalog projection (<see cref="TypeListItem"/>).
/// the base entity is <see cref="NodeType"/>; <see cref="Node"/> is inner-joined so that
/// only types with at least one referencing node are returned.
/// </summary>
public class TypeListMapper : FieldMapper<TypeListItem, NodeType>
{

    /// <summary>
    /// creates a new <see cref="TypeListMapper"/>
    /// </summary>
    public TypeListMapper()
        : base(Mappings()) { }


    /// <inheritdoc />
    public override string[] DefaultListFields => ["id", "type", "count"];


    static IEnumerable<FieldMapping<TypeListItem>> Mappings()
    {
        yield return new FieldMapping<TypeListItem, long>("id",
                                                          DB.Property<NodeType>(t => t.Id, "type"),
                                                          (item, v) => item.Id = v);
        yield return new FieldMapping<TypeListItem, string>("type",
                                                            DB.Property<NodeType>(t => t.Type, "type"),
                                                            (item, v) => item.Type = v);
        yield return new FieldMapping<TypeListItem, long>("count",
                                                          DB.Count(DB.Property<Node>(n => n.Id, "node")),
                                                          (item, v) => item.Count = v);
    }


    /// <inheritdoc />
    public override LoadOperation<NodeType> CreateOperation(IEntityManager database, params IDBField[] fields)
    {
        return database.Load<NodeType>(fields)
                       .Alias("type")
                       .Join<Node>((t, n) => n.TypeId == t.Id, "node");
    }
}
