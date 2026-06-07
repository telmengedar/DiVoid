namespace Backend.Models.Organizations;

/// <summary>
/// API-facing representation of an <see cref="Organization"/>.
/// list responses omit <see cref="Members"/>; only single-get populates it.
/// </summary>
public class OrganizationDetails
{

    /// <summary>
    /// id of organization
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// display name of the organization
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// DiVoid user-id of the organization's creator; 0 for the bootstrap row
    /// </summary>
    public long OwnerId { get; set; }

    /// <summary>
    /// UTC timestamp when the organization row was created
    /// </summary>
    public DateTime? Created { get; set; }

    /// <summary>
    /// UTC timestamp of the last metadata change to the organization row
    /// </summary>
    public DateTime? LastUpdate { get; set; }

    /// <summary>
    /// member user-ids; populated only by single-get responses, omitted from list responses
    /// </summary>
    public long[] Members { get; set; }
}
