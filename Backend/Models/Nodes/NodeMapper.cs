using System.Runtime.CompilerServices;
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
    public override string[] DefaultListFields => ["id", "type", "name", "status"];

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
    }

    /// <summary>
    /// maps raw <see cref="Node"/> entities from a paged result to <see cref="NodeDetails"/>
    /// by resolving type ids against a pre-fetched type lookup.
    ///
    /// The type dictionary is loaded once at the start of enumeration — a single tiny query
    /// against the <see cref="NodeType"/> table.  Everything after that is in-process mapping;
    /// no per-item round trips.  This lets callers wire <see cref="Pooshit.Ocelot.Entities.Operations.Prepared.PagedResult{T}.Items"/>
    /// directly to <see cref="Pooshit.AspNetCore.Services.Formatters.DataStream.AsyncPageResponseWriter{T}"/>
    /// while still getting the correct type string in the output.
    /// </summary>
    /// <param name="database">entity manager used to load the type dictionary</param>
    /// <param name="rawItems">raw node entities from <c>PagedResult&lt;Node&gt;.Items</c></param>
    /// <param name="fields">DTO fields to populate (subset may omit "type" entirely)</param>
    /// <param name="ct">cancellation token threaded from the HTTP request</param>
    /// <returns>async stream of <see cref="NodeDetails"/> with type strings resolved</returns>
    public async IAsyncEnumerable<NodeDetails> EntitiesFromPaged(
        IEntityManager database,
        IAsyncEnumerable<Node> rawItems,
        string[] fields,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        bool includeType = fields.Contains("type");

        // Load type dict once — NodeType table is tiny (dozens of rows max)
        Dictionary<long, string> types = new();
        if (includeType)
        {
            await foreach (NodeType t in database.Load<NodeType>(nt => nt.Id, nt => nt.Type)
                                                 .ExecuteEntitiesAsync().WithCancellation(ct))
                types[t.Id] = t.Type;
        }

        await foreach (Node node in rawItems.WithCancellation(ct))
        {
            NodeDetails dto = new();

            if (fields.Contains("id"))
                dto.Id = node.Id;
            if (includeType && types.TryGetValue(node.TypeId, out string typeName))
                dto.Type = typeName;
            if (fields.Contains("name"))
                dto.Name = node.Name;
            if (fields.Contains("status"))
                dto.Status = node.Status;

            yield return dto;
        }
    }

    /// <inheritdoc />
    public override LoadOperation<Node> CreateOperation(IEntityManager database, params IDBField[] fields)
    {
        return database.Load<Node>(fields)
                        .Alias("node")
                        .Join<NodeType>((n, t) => n.TypeId == t.Id, "type");
    }
}
