using System.Security.Claims;
using Backend.Auth;
using Backend.Errors.Exceptions;
using Backend.Models.Nodes;
using Backend.Services.NodeChanges;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pooshit.AspNetCore.Services.Errors;
using Pooshit.Ocelot.Entities;

namespace Backend.Controllers.V1;

/// <summary>Provides authenticated process-local configuration invalidations.</summary>
/// <param name="feed">The process-local committed publication boundary.</param>
/// <param name="database">The fresh authority source.</param>
[ApiController, Route("api/v1/nodechanges")]
public sealed class NodeChangesController(NodeChangeFeed feed, IEntityManager database) : ControllerBase {
    /// <summary>Streams empty invalidations after a fenced current-authority registration.</summary>
    /// <param name="filter">The direct root, exact selectors and categories.</param>
    /// <param name="cancellationToken">The connection cancellation.</param>
    /// <returns>The live response until cancellation or lost continuity.</returns>
    [HttpGet, Authorize(Policy = "read")]
    [Produces("text/event-stream")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400, Type = typeof(ErrorResponse))]
    [ProducesResponseType(401, Type = typeof(ErrorResponse))]
    [ProducesResponseType(403, Type = typeof(ErrorResponse))]
    [ProducesResponseType(404, Type = typeof(ErrorResponse))]
    [ProducesResponseType(429, Type = typeof(ErrorResponse))]
    [ProducesResponseType(503, Type = typeof(ErrorResponse))]
    [ProducesResponseType(500, Type = typeof(ErrorResponse))]
    public async Task Get([FromQuery] NodeChangeFilter filter, CancellationToken cancellationToken) {
        ValidateQuery();
        ClaimsIdentity identity = User.Identities.FirstOrDefault(candidate => candidate.IsAuthenticated && candidate.AuthenticationType == ApiKeyAuthenticationHandler.SchemeName);
        if (identity == null || !long.TryParse(identity.FindFirst("divoid.api_key_id")?.Value, out long keyId) || keyId <= 0 ||
            !long.TryParse(identity.FindFirst("divoid.user_id")?.Value, out long userId) || userId <= 0)
            throw new AuthenticationFailedException("An authenticated API key is required for graph changes.");
        await using NodeChangeSubscription subscription = await feed.SubscribeAsync(database, keyId, userId, filter, cancellationToken);
        await subscription.StreamAsync(HttpContext, cancellationToken);
    }

    void ValidateQuery() {
        if (Request.Headers.ContainsKey("Last-Event-ID"))
            throw new ArgumentException("Replay is unsupported; reconnect and read a fresh snapshot.");
        if (Request.Query["rootNodeId"].Count != 1 || Request.Query["events"].Count > 1)
            throw new ArgumentException("One direct root and at most one event mask are required.");
        string[] accepted = ["rootNodeId", "id", "type", "events", "cursor", "resume"];
        foreach (string key in Request.Query.Keys)
            if (!accepted.Contains(key, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("An unknown change-stream query field was supplied.");
        if (Request.Query["cursor"].Any(value => !string.IsNullOrEmpty(value)) || Request.Query["resume"].Any(value => !string.IsNullOrEmpty(value)))
            throw new ArgumentException("Replay is unsupported; reconnect and read a fresh snapshot.");
    }
}
