using System.Net;
using Backend.Errors.Exceptions;
using Backend.Models.Auth;
using Backend.Models.Nodes;
using Backend.Models.Users;
using Microsoft.Data.Sqlite;
using Npgsql;
using Pooshit.AspNetCore.Services.Errors.Exceptions;
using Pooshit.Json;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Clients;

namespace Backend.Services.NodeChanges;

/// <summary>Coordinates process-local committed graph invalidations and stream authority.</summary>
public sealed class NodeChangeFeed : IHostedService {
    readonly SemaphoreSlim gate = new(1, 1);
    readonly HashSet<NodeChangeSubscription> subscriptions = [];
    long generation;
    bool stopping;

    internal async Task<long> CaptureGenerationAsync(CancellationToken cancellationToken = default) {
        await gate.WaitAsync(cancellationToken);
        try {
            return generation;
        } finally {
            gate.Release();
        }
    }

    internal async Task<NodeChangeSubscription> SubscribeAsync(IEntityManager database, long keyId, long userId, NodeChangeFilter request, CancellationToken cancellationToken) {
        NodeChangeFilter filter = request.Validate();
        for (int attempt = 0; attempt < 4; ++attempt) {
            long observed = await CaptureGenerationAsync(cancellationToken);
            NodeChangeAuthority authority;
            try {
                authority = await ReadAuthorityAsync(database, keyId, userId, filter);
            } catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6) {
                throw Busy();
            }
            NodeChangeSubscription subscription = null;
            Exception failure = null;
            bool changed;
            await gate.WaitAsync(cancellationToken);
            try {
                changed = observed != generation;
                if (!changed) {
                    failure = authority.Failure;
                    if (stopping)
                        failure = new NodeChangeException(HttpStatusCode.ServiceUnavailable, "graph_change_unavailable", "The change stream is unavailable.");
                    else if (authority.ExpiresAt.HasValue && authority.ExpiresAt.Value <= DateTime.UtcNow)
                        failure = new AuthenticationFailedException("The API key is no longer eligible.");
                    if (failure == null) {
                        if (subscriptions.Count >= 128 || subscriptions.Count(existing => existing.KeyId == keyId) >= 8)
                            failure = new NodeChangeException(HttpStatusCode.TooManyRequests, "graph_change_capacity", "The change-stream connection limit is reached.");
                        else {
                            subscription = new NodeChangeSubscription(this, keyId, userId, authority.Admin, authority.ExpiresAt, filter);
                            subscriptions.Add(subscription);
                        }
                    }
                }
            } finally {
                gate.Release();
            }
            if (changed)
                continue;
            if (failure != null)
                throw failure;
            subscription.ArmExpiry();
            return subscription;
        }
        throw Busy();
    }

    static NodeChangeException Busy() => new(HttpStatusCode.ServiceUnavailable, "graph_change_registration_busy", "A stable registration could not be established. Reconnect and resynchronize.");

    static async Task<NodeChangeAuthority> ReadAuthorityAsync(IEntityManager database, long keyId, long userId, NodeChangeFilter filter) {
        ApiKey key = await database.Load<ApiKey>(row => row.Id, row => row.UserId, row => row.Enabled, row => row.Permissions, row => row.ExpiresAt)
            .Where(row => row.Id == keyId).ExecuteEntityAsync();
        User user = await database.Load<User>(row => row.Id, row => row.Enabled).Where(row => row.Id == userId).ExecuteEntityAsync();
        if (key == null || key.UserId != userId || !key.Enabled || user == null || !user.Enabled || key.ExpiresAt <= DateTime.UtcNow)
            return new NodeChangeAuthority(false, key?.ExpiresAt, new AuthenticationFailedException("The API key is no longer eligible."));
        string[] permissions = string.IsNullOrEmpty(key.Permissions) ? [] : Json.Read<string[]>(key.Permissions) ?? [];
        bool admin = permissions.Contains("admin", StringComparer.Ordinal);
        if (!admin && !permissions.Contains("read", StringComparer.Ordinal) && !permissions.Contains("write", StringComparer.Ordinal))
            return new NodeChangeAuthority(false, key.ExpiresAt, new AuthorizationFailedException("The API key requires read permission."));
        foreach (long id in filter.Id.Append(filter.RootNodeId).Distinct()) {
            Node node = await database.Load<Node>(row => row.Id, row => row.RootNodeId, row => row.OwnerId, row => row.Access)
                .Where(row => row.Id == id).ExecuteEntityAsync();
            if (node == null || !new NodeChangeFact(node.Id, node.RootNodeId, null, node.OwnerId, node.Access).Readable(userId, admin) ||
                id != filter.RootNodeId && node.RootNodeId != filter.RootNodeId)
                return new NodeChangeAuthority(admin, key.ExpiresAt, new NotFoundException<Node>("Requested scope is unavailable."));
        }
        return new NodeChangeAuthority(admin, key.ExpiresAt, null);
    }

    internal async Task CommitAsync(Action commit, IReadOnlyList<NodeChangeDelta> changes, long? userId = null, long? keyId = null, CancellationToken cancellationToken = default) {
        List<NodeChangeSubscription> closed = [];
        bool confirmed = false;
        await gate.WaitAsync(cancellationToken);
        try {
            try {
                commit();
                confirmed = true;
                ++generation;
                foreach (NodeChangeSubscription subscription in subscriptions) {
                    if (subscription.UserId == userId || subscription.KeyId == keyId)
                        subscription.Close();
                    if (!subscription.Closed)
                        foreach (NodeChangeDelta delta in changes)
                            subscription.Apply(delta);
                    if (subscription.Closed)
                        closed.Add(subscription);
                }
                subscriptions.ExceptWith(closed);
            } catch (Exception exception) {
                if (confirmed || !SerializationFailure(exception)) {
                    ++generation;
                    CloseAll(closed);
                }
                throw;
            }
        } finally {
            gate.Release();
            foreach (NodeChangeSubscription subscription in closed)
                subscription.Cancel();
        }
    }

    internal async Task UpdateAuthorityAsync(IEntityManager database, Func<Transaction, Task<bool>> prepare, long? userId = null, long? keyId = null) {
        Transaction transaction = database.Transaction();
        NpgsqlConnection connection = transaction.DbTransaction.Connection as NpgsqlConnection;
        Exception failure = null;
        try {
            bool changed = await prepare(transaction);
            await CommitAsync(transaction.Commit, [], changed ? userId : null, changed ? keyId : null);
        } catch (Exception exception) {
            failure = exception;
            throw;
        } finally {
            await DisposeTransactionAsync(transaction, connection, failure != null);
        }
    }

    internal async Task DisposeTransactionAsync(Transaction transaction, NpgsqlConnection connection, bool failed) {
        try {
            transaction.Dispose();
        } catch (InvalidOperationException) when (connection != null && failed) {
            // Pooshit.Ocelot 0.25.0-preview.1 rolls back completed Npgsql transactions during disposal (#24312).
            transaction.DbTransaction.Dispose();
            connection.Dispose();
        } catch {
            await ResetAsync();
            throw;
        }
    }

    internal static bool SerializationFailure(Exception exception) {
        for (Exception current = exception; current != null; current = current.InnerException)
            if (current is PostgresException postgres)
                return postgres.SqlState == PostgresErrorCodes.SerializationFailure;
        return false;
    }

    internal async Task<bool> AdmitAsync(NodeChangeSubscription subscription, CancellationToken cancellationToken) {
        bool admitted;
        bool cancel = false;
        await gate.WaitAsync(cancellationToken);
        try {
            admitted = !stopping && !subscription.Closed && !subscription.Expired;
            if (!admitted && subscriptions.Remove(subscription)) {
                subscription.Close();
                cancel = true;
            }
        } finally {
            gate.Release();
        }
        if (cancel)
            subscription.Cancel();
        return admitted;
    }

    internal async Task RemoveAsync(NodeChangeSubscription subscription) {
        bool removed;
        await gate.WaitAsync();
        try {
            removed = subscriptions.Remove(subscription);
            if (removed)
                subscription.Close();
        } finally {
            gate.Release();
        }
        if (removed)
            subscription.Cancel();
    }

    internal async Task ResetAsync() {
        List<NodeChangeSubscription> closed = [];
        await gate.WaitAsync();
        try {
            ++generation;
            CloseAll(closed);
        } finally {
            gate.Release();
        }
        foreach (NodeChangeSubscription subscription in closed)
            subscription.Cancel();
    }

    void CloseAll(List<NodeChangeSubscription> closed) {
        foreach (NodeChangeSubscription subscription in subscriptions) {
            subscription.Close();
            closed.Add(subscription);
        }
        subscriptions.Clear();
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken) {
        await gate.WaitAsync(cancellationToken);
        try {
            stopping = true;
        } finally {
            gate.Release();
        }
        await ResetAsync();
    }
}
