using System.Security.Claims;
using Backend.Auth;
using Backend.Errors.Exceptions;
using Backend.Models.Nodes;
using Backend.Services.NodeChanges;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Pooshit.AspNetCore.Services.Errors;
using Pooshit.Ocelot.Entities;

namespace Backend.Controllers.V1;

/// <summary>Provides authenticated process-local configuration invalidations.</summary>
/// <param name="feed">The process-local committed publication boundary.</param>
/// <param name="database">The fresh authority source.</param>
[ApiController, Route("api/v1/nodechanges")]
public sealed class NodeChangesController(NodeChangeFeed feed, IEntityManager database) : ControllerBase {
    const string Ready = "event:ready\ndata:{\"version\":1,\"coverage\":\"this-process\",\"replay\":false,\"resyncRequired\":true}\n\n";
    const string Invalidate = "event:invalidate\ndata:{}\n\n";

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
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Accel-Buffering"] = "no";
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, subscription.Cancellation);
        try {
            if (!await WriteAsync(subscription, Ready, lifetime.Token))
                return;
            Task<bool> change = subscription.Reader.WaitToReadAsync(lifetime.Token).AsTask();
            Task heartbeat = Task.Delay(TimeSpan.FromSeconds(15), lifetime.Token);
            while (!lifetime.IsCancellationRequested) {
                Task completed = await Task.WhenAny(change, heartbeat);
                if (completed == change) {
                    if (!await change)
                        break;
                    if (subscription.Reader.TryRead(out bool dirty) && !await WriteAsync(subscription, Invalidate, lifetime.Token))
                        break;
                    change = subscription.Reader.WaitToReadAsync(lifetime.Token).AsTask();
                } else {
                    await heartbeat;
                    if (!await WriteAsync(subscription, ":keepalive\n\n", lifetime.Token))
                        break;
                    heartbeat = Task.Delay(TimeSpan.FromSeconds(15), lifetime.Token);
                }
            }
        } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) {
        } catch (TimeoutException) {
            HttpContext.Abort();
        } finally {
            await lifetime.CancelAsync();
        }
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

    async Task<bool> WriteAsync(NodeChangeSubscription subscription, string frame, CancellationToken cancellationToken) {
        if (!await feed.AdmitAsync(subscription, cancellationToken))
            return false;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try {
            await Response.WriteAsync(frame, deadline.Token).WaitAsync(deadline.Token);
            await Response.Body.FlushAsync(deadline.Token).WaitAsync(deadline.Token);
        } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
            throw new TimeoutException("The change-stream writer did not complete within its bound.");
        }
        return true;
    }
}
