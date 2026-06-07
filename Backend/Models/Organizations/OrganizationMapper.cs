using Pooshit.Ocelot.Fields;

namespace Backend.Models.Organizations;

/// <summary>
/// mapper translating between <see cref="Organization"/> entities and <see cref="OrganizationDetails"/> DTOs
/// </summary>
public class OrganizationMapper : FieldMapper<OrganizationDetails, Organization>
{

    /// <summary>
    /// creates a new <see cref="OrganizationMapper"/>
    /// </summary>
    public OrganizationMapper()
    : base(Mappings()) { }

    /// <inheritdoc />
    public override string[] DefaultListFields => ["id", "name", "ownerId", "created", "lastupdate"];

    static IEnumerable<FieldMapping<OrganizationDetails>> Mappings()
    {
        yield return new FieldMapping<OrganizationDetails, long>("id",
                                                                 o => o.Id,
                                                                 (o, v) => o.Id = v);
        yield return new FieldMapping<OrganizationDetails, string>("name",
                                                                    o => o.Name,
                                                                    (o, v) => o.Name = v);
        yield return new FieldMapping<OrganizationDetails, long>("ownerId",
                                                                  o => o.OwnerId,
                                                                  (o, v) => o.OwnerId = v);
        yield return new FieldMapping<OrganizationDetails, DateTime>("created",
                                                                      o => o.Created.GetValueOrDefault(),
                                                                      (o, v) => o.Created = v);
        yield return new FieldMapping<OrganizationDetails, DateTime>("lastupdate",
                                                                      o => o.LastUpdate.GetValueOrDefault(),
                                                                      (o, v) => o.LastUpdate = v);
    }
}
