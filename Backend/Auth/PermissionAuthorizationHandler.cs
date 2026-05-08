using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;

namespace Backend.Auth;

/// <summary>
/// authorization handler that resolves the permission implication chain:
/// admin ⇒ write ⇒ read
/// </summary>
public class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement> {
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement) {
        if (SatisfiesPermission(context, requirement.Permission))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }

    static bool SatisfiesPermission(AuthorizationHandlerContext context, string required) {
        // admin implies write implies read
        return required switch {
            "read"  => HasPermission(context, "read")  || HasPermission(context, "write") || HasPermission(context, "admin"),
            "write" => HasPermission(context, "write") || HasPermission(context, "admin"),
            "admin" => HasPermission(context, "admin"),
            _       => HasPermission(context, required)
        };
    }

    static bool HasPermission(AuthorizationHandlerContext context, string permission)
        => context.User.HasClaim("permission", permission);
}
