using Backend.Models.Nodes;
using Backend.Models.Users;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Expressions;
using Pooshit.Ocelot.Fields;
using Pooshit.Ocelot.Tokens;

namespace Backend.Services.Nodes;

/// <summary>Builds node access predicates and evaluates snapshot access.</summary>
static class NodeAuthorization {
    internal static PredicateExpression<Node> BuildVisibilityPredicate(IEntityManager database, long callerId, bool isAdmin, bool write = false) {
        if (isAdmin)
            return null;
        NodeAccess global = write ? NodeAccess.Write : NodeAccess.Read;
        NodeAccess organization = write ? NodeAccess.OrgWrite : NodeAccess.OrgRead;
        LoadOperation<User> memberships = database.Load<User>(u => u.OrganizationId)
            .Where(u => u.Id == callerId && u.Enabled && u.OrganizationId != null);
        return new(n => n.OwnerId == callerId || (n.Access & global) != 0 ||
            ((n.Access & organization) != 0 && n.OrganizationId != null && DB.Property<Node>(n => n.OrganizationId).Int64.In(memberships)));
    }

    internal static bool IsAllowed(long ownerId, NodeAccess access, long? nodeOrganizationId, long callerId, bool isAdmin, long? userOrganizationId, bool write = false) =>
        isAdmin || ownerId == callerId || (access & (write ? NodeAccess.Write : NodeAccess.Read)) != 0 ||
        (userOrganizationId.HasValue && nodeOrganizationId == userOrganizationId &&
         (access & (write ? NodeAccess.OrgWrite : NodeAccess.OrgRead)) != 0);

    internal static PredicateExpression<Node> BuildOwnerPredicate(long callerId, bool isAdmin) =>
        isAdmin ? null : new(n => n.OwnerId == callerId);
}
