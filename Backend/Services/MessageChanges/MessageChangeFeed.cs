using System.Net;
using Backend.Errors.Exceptions;
using Backend.Services.Auth;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Info;
using Npgsql;

namespace Backend.Services.MessageChanges;

/// <summary>Owns the bounded PostgreSQL recipient-listener lifetimes of this API process.</summary>
public sealed class MessageChangeFeed : IHostedService {
    readonly object gate = new();
    readonly HashSet<MessageChangeSubscription> subscriptions = [];
    bool stopping;

    internal async Task<MessageChangeSubscription> SubscribeAsync(IEntityManager database, IApiKeyService keys, HttpContext context, long keyId, long userId, CancellationToken cancellationToken) {
        if (database.DBClient.DBInfo is not PostgreInfo)
            throw Failure(HttpStatusCode.ServiceUnavailable, "inbox_changes_provider_unsupported", "Inbox changes require PostgreSQL notification support.");
        MessageChangeSubscription subscription;
        lock (gate) {
            if (stopping)
                throw Failure(HttpStatusCode.ServiceUnavailable, "inbox_changes_unavailable", "The inbox change stream is unavailable.");
            if (subscriptions.Count >= 8 || subscriptions.Count(existing => existing.KeyId == keyId) >= 2)
                throw Failure(HttpStatusCode.TooManyRequests, "inbox_changes_capacity", "The inbox change-stream connection limit is reached.");
            subscription = new MessageChangeSubscription(this, database, keys, context, keyId, userId);
            subscriptions.Add(subscription);
        }
        try {
            await subscription.StartAsync(cancellationToken);
            return subscription;
        } catch (Exception exception) when (exception is NpgsqlException or TimeoutException or InvalidOperationException) {
            await subscription.DisposeAsync();
            throw Failure(HttpStatusCode.ServiceUnavailable, "inbox_changes_unavailable", "The inbox notification listener is unavailable.");
        } catch {
            await subscription.DisposeAsync();
            throw;
        }
    }

    internal void Remove(MessageChangeSubscription subscription) {
        lock (gate)
            subscriptions.Remove(subscription);
    }

    static NodeChangeException Failure(HttpStatusCode status, string code, string text) => new(status, code, text);

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken) {
        MessageChangeSubscription[] active;
        lock (gate) {
            stopping = true;
            active = subscriptions.ToArray();
        }
        foreach (MessageChangeSubscription subscription in active)
            await subscription.DisposeAsync();
    }
}
