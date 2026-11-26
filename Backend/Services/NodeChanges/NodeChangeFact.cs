using Backend.Models.Nodes;
using Backend.Services.Nodes;

namespace Backend.Services.NodeChanges;

internal sealed record NodeChangeFact(long Id, long? RootNodeId, string Type, long OwnerId, NodeAccess Access, long? OrganizationId = null) {
    internal bool Readable(long userId, bool admin, long? userOrganizationId) =>
        NodeAuthorization.IsAllowed(OwnerId, Access, OrganizationId, userId, admin, userOrganizationId);
}
