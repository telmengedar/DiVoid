using Backend.Models.Nodes;
using Pooshit.Ocelot.Expressions;

namespace Backend.Services.Nodes;

/// <summary>
/// builds SQL visibility predicates for per-node access checks.
/// single source of truth — list visibility and every single-node operation
/// compose the predicate from here so the truth table lives in one place.
/// </summary>
static class NodeAuthorization
{
    /// <summary>
    /// returns a <see cref="PredicateExpression{T}"/> that gates the caller's access to a node.
    /// returns null when the caller is admin (no extra predicate needed — all rows match).
    /// AND this into the operation's WHERE before the single <c>Where()</c> call.
    /// </summary>
    /// <param name="callerId">DiVoid user-id of the authenticated caller (0 = sentinel / auth-disabled)</param>
    /// <param name="isAdmin">true when the caller holds the admin permission</param>
    /// <param name="write">true for write operations (PATCH, POST content, DELETE); false for read (GET, list)</param>
    public static PredicateExpression<Node> BuildVisibilityPredicate(long callerId, bool isAdmin, bool write = false)
    {
        if (isAdmin) return null;
        return write
            ? new PredicateExpression<Node>(n => n.OwnerId == callerId || (n.Access & NodeAccess.Write) != 0)
            : new PredicateExpression<Node>(n => n.OwnerId == callerId || (n.Access & NodeAccess.Read) != 0);
    }

    /// <summary>
    /// returns a <see cref="PredicateExpression{T}"/> that gates write access to the
    /// <see cref="Node.Access"/> property specifically: owner-or-admin only, write-public does NOT
    /// grant permission to flip <c>Access</c> on someone else's node.
    /// returns null when the caller is admin.
    /// </summary>
    public static PredicateExpression<Node> BuildOwnerPredicate(long callerId, bool isAdmin)
    {
        if (isAdmin) return null;
        return new PredicateExpression<Node>(n => n.OwnerId == callerId);
    }
}
