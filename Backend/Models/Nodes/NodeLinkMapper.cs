using Pooshit.Ocelot.Fields;

namespace Backend.Models.Nodes;

/// <summary>
/// mapper for <see cref="NodeLink"/> rows listed by <c>GET /api/nodes/links</c>.
/// its keys are the columns a client can sort by and the columns the list returns.
/// </summary>
public class NodeLinkMapper : FieldMapper<NodeLink, NodeLink> {

    /// <summary>
    /// creates a new <see cref="NodeLinkMapper"/>
    /// </summary>
    public NodeLinkMapper()
        : base(Mappings()) {
    }


    static IEnumerable<FieldMapping<NodeLink>> Mappings() {
        yield return new FieldMapping<NodeLink, long>("sourceid",
                                                      l => l.SourceId,
                                                      (l, v) => l.SourceId = v);
        yield return new FieldMapping<NodeLink, long>("targetid",
                                                      l => l.TargetId,
                                                      (l, v) => l.TargetId = v);
        yield return new FieldMapping<NodeLink, LinkType>("linktype",
                                                          l => l.LinkType,
                                                          (l, v) => l.LinkType = v);
        yield return new FieldMapping<NodeLink, string>("context",
                                                        l => l.Context,
                                                        (l, v) => l.Context = v);
    }
}
