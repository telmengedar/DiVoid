using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Ocelot.Entities.Attributes;

namespace Backend.Models.Organizations;

/// <summary>An organization with an optional graph anchor.</summary>
[AllowPatch]
public class Organization {
    /// <summary>The organization identifier.</summary>
    [PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    /// <summary>The organization name.</summary>
    [AllowPatch]
    public string Name { get; set; }

    /// <summary>The optional graph anchor identifier.</summary>
    [AllowPatch]
    public long? HomeNodeId { get; set; }
}
