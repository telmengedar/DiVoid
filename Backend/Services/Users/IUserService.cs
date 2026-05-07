using Backend.Models.Users;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.AspNetCore.Services.Patches;

namespace Backend.Services.Users;

/// <summary>
/// service used to manage users
/// </summary>
public interface IUserService {

    /// <summary>
    /// creates a new user
    /// </summary>
    /// <param name="parameters">parameters for the user to create</param>
    /// <returns>created user details</returns>
    Task<UserDetails> CreateUser(UserParameters parameters);

    /// <summary>
    /// get a user by id
    /// </summary>
    /// <param name="userId">id of user to get</param>
    /// <returns>user details</returns>
    Task<UserDetails> GetUserById(long userId);

    /// <summary>
    /// lists existing users
    /// </summary>
    /// <param name="filter">filter to apply</param>
    /// <returns>page of users matching filter</returns>
    AsyncPageResponseWriter<UserDetails> ListUsers(ListFilter filter = null);

    /// <summary>
    /// updates properties of an existing user
    /// </summary>
    /// <param name="userId">id of user to update</param>
    /// <param name="patches">patches to apply</param>
    /// <returns>updated user details</returns>
    Task<UserDetails> UpdateUser(long userId, params PatchOperation[] patches);

    /// <summary>
    /// deletes an existing user
    /// </summary>
    /// <param name="userId">id of user to delete</param>
    Task DeleteUser(long userId);

    /// <summary>
    /// determines whether any admin user has an email address set
    /// </summary>
    /// <returns>true if at least one admin user has an email, false otherwise</returns>
    Task<bool> AnyAdminHasEmail();
}
