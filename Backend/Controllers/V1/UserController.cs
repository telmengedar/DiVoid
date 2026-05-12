using Backend.Auth;
using Backend.Models.Users;
using Backend.Services.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.AspNetCore.Services.Patches;

namespace Backend.Controllers.V1;

/// <summary>
/// controller used to manage users
/// </summary>
[Route("api/users")]
[ApiController]
public class UserController(ILogger<UserController> logger, IUserService userService) : ControllerBase {
    readonly ILogger<UserController> logger = logger;
    readonly IUserService userService = userService;

    /// <summary>
    /// returns the full details of the currently authenticated user.
    /// Reachable by any authenticated principal (JWT or API key) regardless of permissions.
    /// For JWT-authenticated requests the permissions array reflects the owning user's
    /// permission set; for API-key-authenticated requests it reflects the key's own
    /// permission set, which may differ from the user's.
    /// </summary>
    /// <returns>full details of the authenticated user</returns>
    [HttpGet("me")]
    [Authorize]
    public async Task<UserDetails> GetMe() {
        long userId = User.GetDivoidUserId();
        UserDetails details = await userService.GetUserById(userId);
        // Permissions in the principal's claims reflect the *effective* permission set:
        // the user's own permissions for JWT principals and the key's own (potentially
        // narrower) permission set for API-key principals.  Override the DB-loaded value
        // so the response is consistent with what PermissionAuthorizationHandler sees.
        details.Permissions = User.FindAll("permission")
                                  .Select(c => c.Value)
                                  .ToArray();
        return details;
    }


    /// <summary>
    /// creates a new user
    /// </summary>
    /// <param name="parameters">parameters for the user to create</param>
    /// <returns>created user details</returns>
    [HttpPost]
    [Authorize(Policy = "admin")]
    public Task<UserDetails> CreateUser([FromBody] UserParameters parameters) {
        logger.LogInformation("Creating user '{Name}'", parameters.Name);
        return userService.CreateUser(parameters);
    }


    /// <summary>
    /// get a user by id
    /// </summary>
    /// <param name="userId">id of user to get</param>
    /// <returns>user details</returns>
    [HttpGet("{userId:long}")]
    [Authorize(Policy = "admin")]
    public Task<UserDetails> GetUserById(long userId) => userService.GetUserById(userId);


    /// <summary>
    /// lists existing users
    /// </summary>
    /// <param name="filter">paging and field filter</param>
    /// <returns>page of user details</returns>
    [HttpGet]
    [Authorize(Policy = "admin")]
    public Task<AsyncPageResponseWriter<UserDetails>> ListUsers([FromQuery] ListFilter filter) => Task.FromResult(userService.ListUsers(filter));


    /// <summary>
    /// patches an existing user (e.g. disable by setting enabled=false)
    /// </summary>
    /// <param name="userId">id of user to patch</param>
    /// <param name="patches">patches to apply</param>
    /// <returns>updated user details</returns>
    [HttpPatch("{userId:long}")]
    [Authorize(Policy = "admin")]
    public Task<UserDetails> PatchUser(long userId, [FromBody] PatchOperation[] patches) {
        logger.LogInformation("Patching user {UserId}", userId);
        return userService.UpdateUser(userId, patches);
    }


    /// <summary>
    /// deletes a user
    /// </summary>
    /// <param name="userId">id of user to delete</param>
    [HttpDelete("{userId:long}")]
    [Authorize(Policy = "admin")]
    public Task DeleteUser(long userId) {
        logger.LogInformation("Deleting user {UserId}", userId);
        return userService.DeleteUser(userId);
    }
}
