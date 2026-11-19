using Backend.Models.Nodes;

namespace Backend.Services.NodeChanges;

internal sealed record NodeChangeFact(long Id, long? RootNodeId, string Type, long OwnerId, NodeAccess Access) {
    internal bool Readable(long userId, bool admin) => admin || OwnerId == userId || (Access & NodeAccess.Read) != 0;
}
