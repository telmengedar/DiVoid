using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Backend.Extensions;
using Backend.Models.Auth;
using Backend.Models.Users;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Errors.Exceptions;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Json;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Expressions;
using Pooshit.Ocelot.Tokens;

namespace Backend.Services.Users;

/// <inheritdoc />
public class UserService : IUserService {
    readonly IEntityManager database;

    /// <summary>
    /// creates a new <see cref="UserService"/>
    /// </summary>
    /// <param name="database">access to database</param>
    public UserService(IEntityManager database) {
        this.database = database;
    }

    /// <inheritdoc />
    public async Task<UserDetails> CreateUser(UserParameters parameters) {
        DateTime now = DateTime.UtcNow;
        long id = await database.Insert<User>()
                                .Columns(u => u.Name, u => u.Email, u => u.Enabled, u => u.CreatedAt)
                                .Values(parameters.Name, parameters.Email, true, now)
                                .ReturnID()
                                .ExecuteAsync();
        return await GetUserById(id);
    }

    /// <inheritdoc />
    public async Task<UserDetails> GetUserById(long userId) {
        UserMapper mapper = new();
        UserDetails user = await mapper.EntityFromOperation(mapper.CreateOperation(database).Where(u => u.Id == userId));
        if (user == null)
            throw new NotFoundException<User>(userId);
        return user;
    }

    /// <inheritdoc />
    public AsyncPageResponseWriter<UserDetails> ListUsers(ListFilter filter = null) {
        filter ??= new();
        UserMapper mapper = new();
        return new(
            mapper.EntitiesFromOperation(mapper.CreateOperation(database, filter.Fields)),
            () => mapper.CreateOperation(database, DB.Count()).ExecuteScalarAsync<long>(),
            filter.Continue
        );
    }

    /// <inheritdoc />
    public async Task<UserDetails> UpdateUser(long userId, params PatchOperation[] patches) {
        if (await database.Update<User>()
                          .Patch(patches)
                          .Where(u => u.Id == userId)
                          .ExecuteAsync() == 0)
            throw new NotFoundException<User>(userId);
        return await GetUserById(userId);
    }

    /// <inheritdoc />
    public async Task DeleteUser(long userId) {
        if (await database.Delete<User>().Where(u => u.Id == userId).ExecuteAsync() == 0)
            throw new NotFoundException<User>(userId);
    }

    /// <inheritdoc />
    public async Task<bool> AnyAdminHasEmail() {
        // Iterate all keys to find admin ones, then check if their users have emails.
        // Admin key count is trivially small in practice.
        IAsyncEnumerable<ApiKey> keys = database.Load<ApiKey>().ExecuteEntitiesAsync();
        await foreach (ApiKey key in keys) {
            if (string.IsNullOrEmpty(key.Permissions)) continue;
            string[] perms = Json.Read<string[]>(key.Permissions);
            bool isAdmin = false;
            foreach (string p in perms) {
                if (p == "admin") { isAdmin = true; break; }
            }
            if (!isAdmin) continue;

            User user = await database.Load<User>()
                                      .Where(u => u.Id == key.UserId)
                                      .ExecuteEntityAsync();
            if (user != null && !string.IsNullOrEmpty(user.Email))
                return true;
        }
        return false;
    }
}
