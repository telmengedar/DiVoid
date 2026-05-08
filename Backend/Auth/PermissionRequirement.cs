using Microsoft.AspNetCore.Authorization;

namespace Backend.Auth;

/// <summary>
/// requirement that the authenticated principal holds a given permission or one that implies it
/// </summary>
public class PermissionRequirement : IAuthorizationRequirement {
    public PermissionRequirement(string permission) {
        Permission = permission;
    }

    public string Permission { get; }
}
