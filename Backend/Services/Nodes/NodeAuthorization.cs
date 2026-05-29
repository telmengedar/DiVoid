using Backend.Models.Nodes;
using Pooshit.Ocelot.Expressions;

namespace Backend.Services.Nodes;

/// <summary>
/// shared authorization helper for per-node access checks.
/// two surfaces — single-node check and list-mode predicate — backed by the same truth table
/// so a single edit moves both in lockstep (drift between these surfaces is a privilege escalation risk).
/// </summary>
static class NodeAuthorization
{
    /// <summary>
    /// returns true when the caller may perform the requested operation on the node.
    /// owner and admin always override; otherwise the node's <see cref="NodeAccess"/> flags decide.
    /// </summary>
    /// <param name="ownerId">node's current OwnerId</param>
    /// <param name="access">node's current Access flags</param>
    /// <param name="callerId">DiVoid user-id of the authenticated caller (0 = sentinel / auth-disabled)</param>
    /// <param name="isAdmin">true when the caller holds the admin permission</param>
    /// <param name="write">true for write operations (PATCH, POST content, DELETE); false for read (GET, list)</param>
    public static bool IsAuthorized(long ownerId, NodeAccess access, long callerId, bool isAdmin, bool write)
    {
        if (isAdmin) return true;
        if (ownerId != 0 && ownerId == callerId) return true;
        return write ? (access & NodeAccess.Write) != 0 : (access & NodeAccess.Read) != 0;
    }

    /// <summary>
    /// returns a <see cref="PredicateExpression{T}"/> that filters out nodes the caller cannot read,
    /// or null when the caller is admin-equivalent (no extra predicate needed).
    /// AND this into the existing filter before the single <c>Where()</c> call.
    /// </summary>
    /// <param name="callerId">DiVoid user-id of the authenticated caller (0 = sentinel / auth-disabled)</param>
    /// <param name="isAdmin">true when the caller holds the admin permission</param>
    public static PredicateExpression<Node> BuildVisibilityPredicate(long callerId, bool isAdmin)
    {
        if (isAdmin) return null;
        return new PredicateExpression<Node>(n => n.OwnerId == callerId || (n.Access & NodeAccess.Read) != 0);
    }

    /// <summary>
    /// returns true when the caller may patch the given path on a node they can already see.
    /// /ownerId is admin-only; /access is owner-or-admin; all other paths are owner-or-admin-or-write-public.
    /// </summary>
    /// <param name="path">JSON patch path (e.g. "/access", "/ownerId", "/name")</param>
    /// <param name="ownerId">node's current OwnerId</param>
    /// <param name="callerId">DiVoid user-id of the authenticated caller</param>
    /// <param name="isAdmin">true when the caller holds the admin permission</param>
    public static bool CanPatchPath(string path, long ownerId, long callerId, bool isAdmin)
    {
        if (string.Equals(path, "/ownerId", StringComparison.OrdinalIgnoreCase))
            return isAdmin;
        bool isOwner = ownerId != 0 && ownerId == callerId;
        if (string.Equals(path, "/access", StringComparison.OrdinalIgnoreCase))
            return isOwner || isAdmin;
        return true;
    }
}
