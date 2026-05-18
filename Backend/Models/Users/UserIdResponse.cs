namespace Backend.Models.Users;

/// <summary>
/// response body for the node → user-id resolver endpoint
/// (<c>GET /api/nodes/{nodeId}/user</c>).
///
/// contains exactly one field — the auth user-id — by deliberate design.
/// callers that need additional user details should call <c>GET /api/users/me</c>
/// (own record) or the appropriate admin endpoint.
///
/// a 404 response means no <see cref="User"/> row has <c>HomeNodeId</c> equal to
/// the requested node-id; this applies whether the node exists or not (the endpoint
/// does not distinguish between a missing node and a missing binding).
/// </summary>
public class UserIdResponse {

    /// <summary>
    /// auth user-id of the user whose <see cref="User.HomeNodeId"/> matches the queried node-id.
    /// never null on a 200 response.
    /// </summary>
    public long UserId { get; set; }
}
