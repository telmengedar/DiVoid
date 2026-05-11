using Backend.Services.Embeddings;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Fields;
using Pooshit.Ocelot.Tokens;
using Pooshit.Ocelot.Tokens.Values;

namespace Backend.Models.Nodes;

/// <summary>
/// mapper for node data
/// </summary>
public class NodeMapper : FieldMapper<NodeDetails, Node>
{
    readonly NodeFilter filter;

    /// <summary>
    /// creates a new <see cref="NodeMapper"/> for standard list mode (no semantic search)
    /// </summary>
    public NodeMapper()
    : this(null) { }

    /// <summary>
    /// creates a new <see cref="NodeMapper"/> optionally configured for semantic search.
    /// when <paramref name="filter"/> carries a non-empty <c>Query</c>, the
    /// <c>similarity</c> field-mapping is included in <see cref="Mappings()"/>.
    /// </summary>
    /// <param name="filter">the inbound node filter; null is treated as standard list mode</param>
    public NodeMapper(NodeFilter filter)
    : base(Mappings(filter))
    {
        this.filter = filter;
    }

    /// <inheritdoc />
    public override string[] DefaultListFields => ["id", "type", "name", "status", "contentType"];

    static IEnumerable<FieldMapping<NodeDetails>> Mappings(NodeFilter filter = null)
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

        if (!string.IsNullOrWhiteSpace(filter?.Query)) {
            // similarity = 1.0 - cosineDistance(queryEmbedding, nodeEmbedding)
            // DB.VCos compiles to pgvector's <=> (cosine distance: smaller = more similar).
            // Both sides are cast to vector so Postgres can invoke the <=> operator.
            // The outer Float cast makes the result a plain float for the .Single projection.
            // Shape taken verbatim from mamgo-backend CampaignItemTargetMapper.cs:171-174.
            string queryText = filter.Query;
            yield return new FieldMapping<NodeDetails, float>("similarity",
                t => 1.0f - DB.Cast(
                    DB.VCos(
                        DB.Value<object>(v => DB.Cast(DB.CustomFunction("embedding",
                                                        DB.Constant(TextContentTypePredicate.EmbeddingModel),
                                                        DB.Constant(queryText)), CastType.Vector)),
                        DB.Value<object>(v => DB.Cast(DB.Property<Node>(n => n.Embedding, "node"), CastType.Vector))),
                    CastType.Float).Single,
                (n, v) => n.Similarity = v);
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
