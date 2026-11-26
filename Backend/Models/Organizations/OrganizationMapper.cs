using Pooshit.Ocelot.Fields;

namespace Backend.Models.Organizations;

/// <summary>Maps organization fields.</summary>
public class OrganizationMapper() : FieldMapper<Organization, Organization>(Mappings()) {
    static IEnumerable<FieldMapping<Organization>> Mappings() {
        yield return new FieldMapping<Organization, long>("id", o => o.Id, (o, v) => o.Id = v);
        yield return new FieldMapping<Organization, string>("name", o => o.Name, (o, v) => o.Name = v);
        yield return new FieldMapping<Organization, long?>("homeNodeId", o => o.HomeNodeId, (o, v) => o.HomeNodeId = v);
    }
}
