using Backend.Errors.Exceptions;
using Backend.Models.Auth;
using Backend.Services.Auth;
using Pooshit.AspNetCore.Services.Errors.Exceptions;

namespace Backend.Services.MessageChanges;

internal static class MessageChangeAuthority {
    internal static async Task<ApiKeyDetails> ReadAsync(HttpContext context, IApiKeyService keys, long keyId, long userId, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        string authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new AuthenticationFailedException("An eligible API key is required for inbox changes.");
        ApiKeyDetails key;
        try {
            key = await keys.GetApiKey(authorization[7..].Trim());
        } catch (Exception exception) when (exception is NotFoundException<ApiKey> or InvalidOperationException) {
            throw new AuthenticationFailedException("The API key is no longer eligible for inbox changes.");
        }
        if (key.Id != keyId || key.UserId != userId || !key.Enabled || key.ExpiresAt <= DateTime.UtcNow)
            throw new AuthenticationFailedException("The API key is no longer eligible for inbox changes.");
        if (!(key.Permissions ?? []).Any(permission => permission is "read" or "write" or "admin"))
            throw new AuthorizationFailedException("The API key requires read permission.");
        cancellationToken.ThrowIfCancellationRequested();
        return key;
    }
}
