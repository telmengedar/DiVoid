using System.Security.Claims;
using System.Threading.Tasks;
using Backend.Models.Auth;

namespace Backend.Services.Auth;

/// <summary>
/// service used to resolve authenticated principal identity
/// </summary>
public interface IAuthService {

    /// <summary>
    /// resolves the identity of the authenticated principal into a <see cref="WhoamiDetails"/> response.
    /// Permissions are read from the principal's claims, so they reflect the key's permissions
    /// for API-key principals and the user's permissions for JWT principals.
    /// </summary>
    /// <param name="principal">authenticated principal from the current request</param>
    /// <returns>identity and permissions of the authenticated principal</returns>
    Task<WhoamiDetails> GetWhoami(ClaimsPrincipal principal);
}
