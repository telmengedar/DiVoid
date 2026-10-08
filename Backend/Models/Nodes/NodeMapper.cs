using System;
using System.Collections.Generic;
using System.Linq;
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
    const string QueryVectorAlias = "q";
    const string QueryVectorColumn = "v";
    const string SimilarityAlias = "sim";
    const string SimilarityColumn = "similarity";
    const string ScaleColumn = "scale";
    const string SumAlias = "qs";

    readonly NodeFilter filter;

    /// <summary>
    /// creates a new <see cref="NodeMapper"/> for standard list mode (no semantic search)
    /// </summary>
    public NodeMapper()
    : this(null) { }

    /// <summary>
    /// creates a new <see cref="NodeMapper"/> optionally configured for semantic search.
    /// when <paramref name="filter"/> carries a non-empty <c>Query</c> or <c>Queries</c>, the
    /// <c>similarity</c> field-mapping is included in <see cref="Mappings()"/>.
    /// </summary>
    /// <param name="filter">the inbound node filter; null is treated as standard list mode</param>
    public NodeMapper(NodeFilter filter)
    : base(Mappings(filter).ToArray(), null, PostProcess)
    {
        this.filter = filter;
    }

    /// <inheritdoc />
    public override string[] DefaultListFields => ["id", "type", "name", "status", "severity", "refinement", "rootNodeId", "contentType", "ownerId", "access", "created", "lastupdate"];

    /// <summary>
    /// post-process callback invoked by <see cref="FieldMapper{TModel}"/> after all field
    /// setters have run for a single row.  encodes <see cref="NodeDetails.RawContent"/> into
    /// <see cref="NodeDetails.Content"/> when the <c>content</c> field was requested.
    /// </summary>
    static void PostProcess(NodeDetails node, string[] fields)
    {
        if (!fields.Contains("content"))
            return;
        node.Content = InlineContentEncoder.Encode(node.RawContent, node.ContentType);
    }

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
        yield return new FieldMapping<NodeDetails, int?>("severity",
                                                        DB.Property<Node>(n => n.Severity, "node"),
                                                        (n, v) => n.Severity = v);
        yield return new FieldMapping<NodeDetails, string>("refinement",
                                                        DB.Property<Node>(n => n.Refinement, "node"),
                                                        (n, v) => n.Refinement = v);
        yield return new FieldMapping<NodeDetails, long?>("rootNodeId",
                                                        DB.Property<Node>(n => n.RootNodeId, "node"),
                                                        (n, v) => n.RootNodeId = v);
        yield return new FieldMapping<NodeDetails, string>("contentType",
                                                        DB.Property<Node>(n => n.ContentType, "node"),
                                                        (n, v) => n.ContentType = v);
        yield return new FieldMapping<NodeDetails, double>("x",
                                                        DB.Property<Node>(n => n.X, "node"),
                                                        (n, v) => n.X = v);
        yield return new FieldMapping<NodeDetails, double>("y",
                                                        DB.Property<Node>(n => n.Y, "node"),
                                                        (n, v) => n.Y = v);
        yield return new FieldMapping<NodeDetails, byte[]>("content",
                                                        DB.Property<Node>(n => n.Content, "node"),
                                                        (n, v) => n.RawContent = v);
        yield return new FieldMapping<NodeDetails, string>("substance",
                                                        DB.Property<Node>(n => n.Substance, "node"),
                                                        (n, v) => n.Substance = v);
        yield return new FieldMapping<NodeDetails, long>("ownerId",
                                                        DB.Property<Node>(n => n.OwnerId, "node"),
                                                        (n, v) => n.OwnerId = v);
        yield return new FieldMapping<NodeDetails, NodeAccess>("access",
                                                        DB.Property<Node>(n => n.Access, "node"),
                                                        (n, v) => n.Access = v);
        yield return new FieldMapping<NodeDetails, DateTime>("created",
                                                        DB.Property<Node>(n => n.Created, "node"),
                                                        (n, v) => n.Created = v);
        yield return new FieldMapping<NodeDetails, DateTime>("lastupdate",
                                                        DB.Property<Node>(n => n.LastUpdate, "node"),
                                                        (n, v) => n.LastUpdate = v);

        if (IsSemantic(filter)) {
            yield return new FieldMapping<NodeDetails, float>("similarity",
                DB.Column(SimilarityAlias, SimilarityColumn),
                (n, v) => n.Similarity = v);
        }
    }

    static bool IsSemantic(NodeFilter filter) => filter?.GetEffectiveQueries().Length > 0;

    static ISqlToken EmbeddedQuery(string queryText)
    {
        return DB.Value<object>(v => DB.Cast(DB.CustomFunction("embedding",
                                                DB.Constant(TextContentTypePredicate.EmbeddingModel),
                                                DB.Constant(queryText)), CastType.Vector));
    }

    ILoadOperation QueryVectorSubselect(IEntityManager database)
    {
        string[] queries = filter.GetEffectiveQueries();
        if (queries.Length == 1)
            return database.Load(DB.As(EmbeddedQuery(queries[0]), QueryVectorColumn))
                           .Offset(0);

        ISqlToken sum = queries
                        .Select(query => DB.CustomFunction("l2_normalize", EmbeddedQuery(query)))
                        .Aggregate((total, next) => DB.CustomFunction("vector_add", total, next));
        ILoadOperation summed = database.Load(DB.As(sum, QueryVectorColumn)).Offset(0);
        float queryCount = queries.Length;
        return database.Load(DB.As(DB.Column(SumAlias, QueryVectorColumn), QueryVectorColumn),
                             DB.As(DB.Value<object>(v => DB.Cast(DB.CustomFunction("vector_norm", DB.Column(SumAlias, QueryVectorColumn)), CastType.Float).Single / queryCount), ScaleColumn))
                       .From(summed)
                       .Alias(SumAlias)
                       .Offset(0);
    }

    ILoadOperation SimilaritySubselect(IEntityManager database)
    {
        if (filter.GetEffectiveQueries().Length > 1)
            return database.Load(DB.As(DB.Value<object>(v => (1.0f - DB.Cast(
                                            DB.VCos(
                                                DB.Column(QueryVectorAlias, QueryVectorColumn),
                                                DB.Value<object>(w => DB.Cast(DB.Property<Node>(n => n.Embedding, "node"), CastType.Vector))),
                                            CastType.Float).Single) * DB.Column(QueryVectorAlias, ScaleColumn).Single), SimilarityColumn))
                           .Offset(0);

        return database.Load(DB.As(DB.Value<object>(v => 1.0f - DB.Cast(
                                        DB.VCos(
                                            DB.Column(QueryVectorAlias, QueryVectorColumn),
                                            DB.Value<object>(w => DB.Cast(DB.Property<Node>(n => n.Embedding, "node"), CastType.Vector))),
                                        CastType.Float).Single), SimilarityColumn))
                       .Offset(0);
    }

    /// <inheritdoc />
    public override LoadOperation<Node> CreateOperation(IEntityManager database, params IDBField[] fields)
    {
        LoadOperation<Node> operation = database.Load<Node>(fields)
                                                .Alias("node")
                                                .Join<NodeType>((n, t) => n.TypeId == t.Id, "type");
        if (!IsSemantic(filter))
            return operation;

        return operation.LateralJoin(QueryVectorSubselect(database), null, QueryVectorAlias)
                        .LateralJoin(SimilaritySubselect(database), null, SimilarityAlias);
    }
}
