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
[Authorize(Policy = "admin")]
public class UserController(ILogger<UserController> logger, IUserService userService) : ControllerBase {
    readonly ILogger<UserController> logger = logger;
    readonly IUserService userService = userService;

    /// <summary>
    /// creates a new user
    /// </summary>
    /// <param name="parameters">parameters for the user to create</param>
    /// <returns>created user details</returns>
    [HttpPost]
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
    public Task<UserDetails> GetUserById(long userId) => userService.GetUserById(userId);

    /// <summary>
    /// lists existing users
    /// </summary>
    /// <param name="filter">paging and field filter</param>
    /// <returns>page of user details</returns>
    [HttpGet]
    public Task<AsyncPageResponseWriter<UserDetails>> ListUsers([FromQuery] ListFilter filter) => Task.FromResult(userService.ListUsers(filter));

    /// <summary>
    /// patches an existing user (e.g. disable by setting enabled=false)
    /// </summary>
    /// <param name="userId">id of user to patch</param>
    /// <param name="patches">patches to apply</param>
    /// <returns>updated user details</returns>
    [HttpPatch("{userId:long}")]
    public Task<UserDetails> PatchUser(long userId, [FromBody] PatchOperation[] patches) {
        logger.LogInformation("Patching user {UserId}", userId);
        return userService.UpdateUser(userId, patches);
    }

    /// <summary>
    /// deletes a user
    /// </summary>
    /// <param name="userId">id of user to delete</param>
    [HttpDelete("{userId:long}")]
    public Task DeleteUser(long userId) {
        logger.LogInformation("Deleting user {UserId}", userId);
        return userService.DeleteUser(userId);
    }
}
