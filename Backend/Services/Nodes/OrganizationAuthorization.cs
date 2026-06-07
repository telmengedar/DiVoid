using Backend.Models.Nodes;
using Pooshit.Ocelot.Expressions;
using Pooshit.Ocelot.Fields;
using Pooshit.Ocelot.Tokens;

namespace Backend.Services.Nodes;

/// <summary>
/// builds the outermost org-scope visibility predicate for node queries.
/// sibling to <see cref="NodeAuthorization"/>; composes BEFORE the per-node access bits.
/// </summary>
static class OrganizationAuthorization
{

    /// <summary>
    /// returns the org-membership predicate; null = admin / no-filter, always-false predicate when membership is empty.
    /// </summary>
    /// <param name="accessibleOrgs">orgs the caller is a member of; null = no filter (admin-equivalent)</param>
    /// <param name="isAdmin">true when the caller holds the admin permission</param>
    public static PredicateExpression<Node> BuildOrgVisibilityPredicate(long[] accessibleOrgs, bool isAdmin)
    {
        if (isAdmin || accessibleOrgs == null) return null;
        if (accessibleOrgs.Length == 0) return new PredicateExpression<Node>(n => false);
        return new PredicateExpression<Node>(n => n.OrganizationId.In(accessibleOrgs));
    }

    /// <summary>
    /// in-process membership check for a single org id; used by <c>CreateNode</c> to validate
    /// a body-supplied <c>organizationId</c> against the caller's accessible set.
    /// </summary>
    /// <param name="orgId">organization id to check; null returns false</param>
    /// <param name="accessibleOrgs">caller's accessible orgs; null = admin-equivalent (returns true)</param>
    /// <param name="isAdmin">true when the caller holds the admin permission</param>
    public static bool IsOrgAccessible(long? orgId, long[] accessibleOrgs, bool isAdmin)
    {
        if (isAdmin || accessibleOrgs == null) return true;
        if (orgId == null) return false;
        long target = orgId.Value;
        foreach (long o in accessibleOrgs)
            if (o == target) return true;
        return false;
    }
}
