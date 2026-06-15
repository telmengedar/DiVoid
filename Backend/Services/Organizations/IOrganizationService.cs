using System.Threading;
using Backend.Models.Organizations;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.AspNetCore.Services.Patches;

namespace Backend.Services.Organizations;

/// <summary>
/// service managing <see cref="Organization"/> rows and the
/// <see cref="UserOrganization"/> membership join.
/// </summary>
public interface IOrganizationService
{

    /// <summary>
    /// creates a new <see cref="Organization"/>; ownership defaults to the calling user.
    /// </summary>
    /// <param name="organization">organization to create</param>
    /// <param name="callerId">DiVoid user-id of the authenticated caller</param>
    /// <returns>created organization</returns>
    Task<OrganizationDetails> CreateOrganization(OrganizationDetails organization, long callerId);

    /// <summary>
    /// returns the organization with the supplied id; 404 when invisible to the caller.
    /// </summary>
    /// <param name="id">organization id</param>
    /// <param name="accessibleOrgs">caller's accessible orgs; null = admin-equivalent</param>
    /// <param name="isAdmin">true when the caller holds the admin permission</param>
    /// <returns>organization details including the <c>Members</c> array</returns>
    Task<OrganizationDetails> GetOrganizationById(long id, long[] accessibleOrgs, bool isAdmin);

    /// <summary>
    /// lists organizations visible to the caller (admin sees all).
    /// </summary>
    /// <param name="filter">organization filter and paging</param>
    /// <param name="accessibleOrgs">caller's accessible orgs; null = admin-equivalent</param>
    /// <param name="isAdmin">true when the caller holds the admin permission</param>
    /// <param name="ct">cancellation token bound to the HTTP request lifetime</param>
    /// <returns>page of organizations matching the filter</returns>
    Task<AsyncPageResponseWriter<OrganizationDetails>> ListPaged(OrganizationFilter filter, long[] accessibleOrgs, bool isAdmin, CancellationToken ct = default);

    /// <summary>
    /// returns the organization-ids that <paramref name="userId"/> is a member of.
    /// used at claim-emission time (JWT path) and at API-key-mint time to snapshot membership.
    /// </summary>
    /// <param name="userId">user id whose memberships to fetch</param>
    /// <returns>array of organization ids; empty when the user has no memberships</returns>
    Task<long[]> GetUserOrganizationIds(long userId);

    /// <summary>
    /// adds <paramref name="userId"/> as a member of <paramref name="orgId"/>; idempotent.
    /// </summary>
    /// <param name="orgId">organization id</param>
    /// <param name="userId">user id to add</param>
    Task AddMember(long orgId, long userId);
}
