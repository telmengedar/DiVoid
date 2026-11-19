using Backend.Models.Nodes;

namespace Backend.Services.NodeChanges;

internal sealed record NodeChangeDelta(NodeChangeFact Before, NodeChangeFact After, NodeHookEvents Events);
