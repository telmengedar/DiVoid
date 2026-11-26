using Backend.Models.Organizations;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.AspNetCore.Services.Patches;

namespace Backend.Services.Organizations;

/// <summary>Manages organizations.</summary>
public interface IOrganizationService {
    /// <summary>Creates an organization.</summary>
    /// <param name="organization">The organization fields.</param>
    /// <returns>The created organization.</returns>
    Task<Organization> Create(Organization organization);

    /// <summary>Reads an organization.</summary>
    /// <param name="id">The organization identifier.</param>
    /// <returns>The organization.</returns>
    Task<Organization> Get(long id);

    /// <summary>Lists organizations.</summary>
    /// <param name="filter">The paging and field filter.</param>
    /// <returns>The organization page.</returns>
    AsyncPageResponseWriter<Organization> List(ListFilter filter);

    /// <summary>Patches an organization.</summary>
    /// <param name="id">The organization identifier.</param>
    /// <param name="patches">The requested changes.</param>
    /// <returns>The updated organization.</returns>
    Task<Organization> Patch(long id, PatchOperation[] patches);

    /// <summary>Deletes an unreferenced organization.</summary>
    /// <param name="id">The organization identifier.</param>
    /// <returns>The asynchronous operation.</returns>
    Task Delete(long id);
}
