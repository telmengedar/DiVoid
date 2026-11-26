using Backend.Models.Organizations;
using Backend.Services.Organizations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.AspNetCore.Services.Patches;

namespace Backend.Controllers.V1;

/// <summary>Administers organizations.</summary>
[ApiController, Route("api/organizations"), Authorize(Policy = "admin")]
[ProducesResponseType(401), ProducesResponseType(403)]
public class OrganizationController(IOrganizationService organizations) : ControllerBase {
    /// <summary>Creates an organization.</summary>
    /// <param name="organization">The organization fields.</param>
    /// <returns>The created organization.</returns>
    [HttpPost, ProducesResponseType(200), ProducesResponseType(400), ProducesResponseType(404)]
    public Task<Organization> Create([FromBody] Organization organization) => organizations.Create(organization);

    /// <summary>Lists organizations.</summary>
    /// <param name="filter">The paging and field filter.</param>
    /// <returns>The organization page.</returns>
    [HttpGet, ProducesResponseType(200)]
    public AsyncPageResponseWriter<Organization> List([FromQuery] ListFilter filter) => organizations.List(filter);

    /// <summary>Reads an organization.</summary>
    /// <param name="id">The organization identifier.</param>
    /// <returns>The organization.</returns>
    [HttpGet("{id:long}"), ProducesResponseType(200), ProducesResponseType(404)]
    public Task<Organization> Get(long id) => organizations.Get(id);

    /// <summary>Patches an organization.</summary>
    /// <param name="id">The organization identifier.</param>
    /// <param name="patches">The requested changes.</param>
    /// <returns>The updated organization.</returns>
    [HttpPatch("{id:long}"), ProducesResponseType(200), ProducesResponseType(400), ProducesResponseType(404)]
    public Task<Organization> Patch(long id, [FromBody] PatchOperation[] patches) => organizations.Patch(id, patches);

    /// <summary>Deletes an unreferenced organization.</summary>
    /// <param name="id">The organization identifier.</param>
    /// <returns>The asynchronous operation.</returns>
    [HttpDelete("{id:long}"), ProducesResponseType(200), ProducesResponseType(400), ProducesResponseType(404)]
    public Task Delete(long id) => organizations.Delete(id);
}
