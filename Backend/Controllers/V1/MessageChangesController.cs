using System.Security.Claims;
using Backend.Auth;
using Backend.Errors.Exceptions;
using Backend.Services.Auth;
using Backend.Services.MessageChanges;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pooshit.AspNetCore.Services.Errors;
using Pooshit.Ocelot.Entities;

namespace Backend.Controllers.V1;

/// <summary>Provides empty committed inbox invalidations for the authenticated API-key recipient.</summary>
/// <param name="feed">The bounded listener owner.</param>
/// <param name="database">The inbox database.</param>
/// <param name="keys">The existing HMAC authority service.</param>
[ApiController, Route("api/v1/messagechanges")]
public sealed class MessageChangesController(MessageChangeFeed feed, IEntityManager database, IApiKeyService keys) : ControllerBase {
    /// <summary>Streams own-recipient hints without exporting message or routing data.</summary>
    /// <param name="cancellationToken">The request lifetime.</param>
    /// <returns>The live stream until cancellation or lost continuity.</returns>
    [HttpGet, Authorize(Policy = "read"), Produces("text/event-stream")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400, Type = typeof(ErrorResponse))]
    [ProducesResponseType(401, Type = typeof(ErrorResponse))]
    [ProducesResponseType(403, Type = typeof(ErrorResponse))]
    [ProducesResponseType(429, Type = typeof(ErrorResponse))]
    [ProducesResponseType(503, Type = typeof(ErrorResponse))]
    public async Task Get(CancellationToken cancellationToken) {
        if (Request.Query.Count != 0 || Request.Headers.ContainsKey("Last-Event-ID"))
            throw new ArgumentException("Inbox changes accept no query scope or replay cursor.");
        ClaimsIdentity identity = User.Identities.FirstOrDefault(candidate => candidate.IsAuthenticated && candidate.AuthenticationType == ApiKeyAuthenticationHandler.SchemeName);
        if (identity == null || !long.TryParse(identity.FindFirst("divoid.api_key_id")?.Value, out long keyId) || keyId <= 0 ||
            !long.TryParse(identity.FindFirst("divoid.user_id")?.Value, out long userId) || userId <= 0)
            throw new AuthenticationFailedException("An authenticated API key is required for inbox changes.");
        await using MessageChangeSubscription subscription = await feed.SubscribeAsync(database, keys, HttpContext, keyId, userId, cancellationToken);
        await subscription.StreamAsync(cancellationToken);
    }
}
