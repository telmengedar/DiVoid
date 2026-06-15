using Pooshit.AspNetCore.Services.Data;

namespace Backend.Models.Organizations;

/// <summary>
/// filter for <see cref="Organization"/> list operations
/// </summary>
public class OrganizationFilter : ListFilter
{

    /// <summary>
    /// organization ids to filter for
    /// </summary>
    public long[] Id { get; set; }

    /// <summary>
    /// names to filter for; wildcard-aware ('%' / '_') per the global string-array convention
    /// </summary>
    public string[] Name { get; set; }
}
