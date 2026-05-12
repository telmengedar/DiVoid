using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Backend.Errors.Exceptions;
using Backend.Models.Auth;
using Backend.Models.Users;
using Pooshit.AspNetCore.Services.Errors.Exceptions;
using Pooshit.Ocelot.Entities;

namespace Backend.Services.Auth;

/// <inheritdoc />
public class AuthService : IAuthService {
    readonly IEntityManager database;

    /// <summary>
    /// creates a new <see cref="AuthService"/>
    /// </summary>
    /// <param name="database">access to database</param>
    public AuthService(IEntityManager database) {
        this.database = database;
    }

    /// <inheritdoc />
    public async Task<WhoamiDetails> GetWhoami(ClaimsPrincipal principal) {
        // For JWT principals the original Keycloak identity carries NameIdentifier = "sub" (a string like
        // "00000000-0000-0000-0000-000000000000") while KeycloakClaimsTransformation adds a SECOND identity
        // whose NameIdentifier is the numeric divoid_user.Id.  FindFirstValue would return the Keycloak sub,
        // so we scan all NameIdentifier claims for one that parses as a long (the DiVoid id).
        // For API-key principals there is only one identity and its NameIdentifier is already numeric.
        long userId = 0;
        bool found = false;
        foreach (Claim claim in principal.FindAll(ClaimTypes.NameIdentifier)) {
            if (long.TryParse(claim.Value, out long parsed)) {
                userId = parsed;
                found  = true;
                break;
            }
        }
        if (!found)
            throw new AuthorizationFailedException("Principal does not carry a valid user identity");

        User user = await database.Load<User>()
                                  .Where(u => u.Id == userId)
                                  .ExecuteEntityAsync();

        if (user == null)
            throw new NotFoundException<User>(userId);

        if (!user.Enabled)
            throw new AuthorizationFailedException("DiVoid account is disabled");

        string[] permissions = principal.FindAll("permission")
                                        .Select(c => c.Value)
                                        .ToArray();

        return new WhoamiDetails {
            UserId      = user.Id,
            Name        = user.Name,
            Email       = user.Email,
            Permissions = permissions
        };
    }
}
