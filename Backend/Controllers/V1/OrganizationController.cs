using System.Threading;
using Backend.Auth;
using Backend.Models.Organizations;
using Backend.Services.Organizations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pooshit.AspNetCore.Services.Formatters.DataStream;

namespace Backend.Controllers.V1;

/// <summary>
/// REST surface for <see cref="Organization"/> rows and the membership join.
/// see <c>docs/architecture/organizations.md</c> §8 for the contract.
/// </summary>
[Route("api/organizations")]
[ApiController]
public class OrganizationController(ILogger<OrganizationController> logger, IOrganizationService organizationService) : ControllerBase
{
    readonly ILogger<OrganizationController> logger = logger;
    readonly IOrganizationService organizationService = organizationService;

    /// <summary>
    /// resolves caller identity from the current principal.
    /// returns (0, true, null) when auth is disabled or the principal carries no identity — admin-equivalent posture.
    /// </summary>
    (long callerId, bool isAdmin, long[] accessibleOrgs) ResolveCaller()
    {
        if (User.Identity?.IsAuthenticated != true)
            return (0L, true, null);
        long callerId = 0L;
        string idValue = User.FindFirst(ClaimsExtensions.DivoidUserIdClaimType)?.Value;
        if (idValue != null)
            long.TryParse(idValue, out callerId);
        bool isAdmin = User.HasClaim("permission", "admin");
        long[] accessibleOrgs = User.GetAccessibleOrgs();
        return (callerId, isAdmin, accessibleOrgs);
    }

    /// <summary>
    /// lists organizations visible to the caller (admin sees all).
    /// </summary>
    /// <param name="filter">organization filter and paging</param>
    /// <param name="ct">cancellation token bound to the HTTP request lifetime</param>
    /// <returns>page of organizations matching the filter</returns>
    [ProducesResponseType(200)]
    [HttpGet]
    [Authorize(Policy = "read")]
    public Task<AsyncPageResponseWriter<OrganizationDetails>> ListPaged([FromQuery] OrganizationFilter filter, CancellationToken ct)
    {
        (_, bool isAdmin, long[] accessibleOrgs) = ResolveCaller();
        return organizationService.ListPaged(filter, accessibleOrgs, isAdmin, ct);
    }

    /// <summary>
    /// returns the organization with the supplied id.
    /// </summary>
    /// <param name="organizationId">organization id</param>
    /// <returns>organization details with the membership list</returns>
    [ProducesResponseType(200)]
    [ProducesResponseType(404)]
    [HttpGet("{organizationId:long}")]
    [Authorize(Policy = "read")]
    public Task<OrganizationDetails> GetOrganizationById(long organizationId)
    {
        (_, bool isAdmin, long[] accessibleOrgs) = ResolveCaller();
        return organizationService.GetOrganizationById(organizationId, accessibleOrgs, isAdmin);
    }

    /// <summary>
    /// creates a new organization; admin only.
    /// </summary>
    /// <param name="organization">organization to create (name required)</param>
    /// <returns>created organization</returns>
    [ProducesResponseType(200)]
    [ProducesResponseType(403)]
    [HttpPost]
    [Authorize(Policy = "admin")]
    public Task<OrganizationDetails> CreateOrganization([FromBody] OrganizationDetails organization)
    {
        logger.LogInformation("Creating organization '{Name}'", organization.Name);
        (long callerId, _, _) = ResolveCaller();
        return organizationService.CreateOrganization(organization, callerId);
    }

    /// <summary>
    /// adds <paramref name="userId"/> as a member of the organization; admin only; idempotent.
    /// </summary>
    /// <param name="organizationId">organization to add the member to</param>
    /// <param name="userId">user-id to add</param>
    [ProducesResponseType(200)]
    [ProducesResponseType(403)]
    [HttpPost("{organizationId:long}/members")]
    [Authorize(Policy = "admin")]
    public Task AddMember(long organizationId, [FromBody] long userId)
    {
        logger.LogInformation("Adding user '{UserId}' to organization '{OrgId}'", userId, organizationId);
        return organizationService.AddMember(organizationId, userId);
    }
}
