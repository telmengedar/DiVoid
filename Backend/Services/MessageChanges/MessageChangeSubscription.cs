using System.Threading.Channels;
using Backend.Models.Auth;
using Backend.Models.Messages;
using Backend.Models.Users;
using Backend.Services.Auth;
using Backend.Services.ChangeStreams;
using Npgsql;
using Pooshit.Ocelot.Entities;

namespace Backend.Services.MessageChanges;

internal sealed class MessageChangeSubscription : IAsyncDisposable {
    const string Ready = "event:ready\ndata:{\"version\":1,\"coverage\":\"postgres-database\",\"replay\":false,\"resyncRequired\":true}\n\n";
    readonly MessageChangeFeed owner;
    readonly IEntityManager database;
    readonly IApiKeyService keys;
    readonly HttpContext context;
    readonly long userId;
    readonly Channel<bool> dirty = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) {
        FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, AllowSynchronousContinuations = false
    });
    readonly CancellationTokenSource cancellation = new();
    NpgsqlConnection connection;
    Task pump = Task.CompletedTask;
    readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<Task> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int closing;
    ChangeStreamDeadline deadline;
    CancellationTokenRegistration requestCancellation;
    string messageChannel;
    string keyChannel;
    string userChannel;
    DateTime? expiresAt;
    int disposed;

    internal MessageChangeSubscription(MessageChangeFeed owner, IEntityManager database, IApiKeyService keys, HttpContext context, long keyId, long userId) {
        this.owner = owner;
        this.database = database;
        this.keys = keys;
        this.context = context;
        this.userId = userId;
        KeyId = keyId;
    }

    internal long KeyId { get; }

    internal async Task StartAsync(CancellationToken cancellationToken) {
        try {
            await InitializeAsync(cancellationToken);
        } finally {
            initialized.TrySetResult();
        }
    }

    async Task InitializeAsync(CancellationToken cancellationToken) {
        requestCancellation = cancellationToken.Register(Close);
        connection = await MessageChangeSchema.OpenAsync(database, cancellation.Token);
        MessageChangeTable messages = await MessageChangeSchema.ResolveAsync<Message>(database, connection);
        MessageChangeTable apiKeys = await MessageChangeSchema.ResolveAsync<ApiKey>(database, connection);
        MessageChangeTable users = await MessageChangeSchema.ResolveAsync<User>(database, connection);
        await MessageChangeSchema.RequireInstalledAsync(connection, [messages, apiKeys, users]);
        messageChannel = MessageChangeSchema.Channel("message", messages.Oid, userId);
        keyChannel = MessageChangeSchema.Channel("key", apiKeys.Oid, KeyId);
        userChannel = MessageChangeSchema.Channel("user", users.Oid, userId);
        connection.Notification += Notify;
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken)) {
            await using NpgsqlCommand command = new(string.Join(";", new[] { messageChannel, keyChannel, userChannel }.Select(channel => "LISTEN " + MessageChangeSchema.Quote(channel))), connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        pump = PumpAsync();
        ApiKeyDetails authority = await MessageChangeAuthority.ReadAsync(context, keys, KeyId, userId, cancellation.Token);
        expiresAt = authority.ExpiresAt;
        deadline = new ChangeStreamDeadline(expiresAt, () => Volatile.Read(ref closing) != 0, Close);
        deadline.Arm();
        cancellation.Token.ThrowIfCancellationRequested();
    }

    void Notify(object sender, NpgsqlNotificationEventArgs notification) {
        if (notification.Payload.Length != 0 || notification.Channel == keyChannel || notification.Channel == userChannel)
            Close();
        else if (notification.Channel == messageChannel)
            dirty.Writer.TryWrite(true);
    }

    async Task PumpAsync() {
        try {
            while (!cancellation.IsCancellationRequested)
                await connection.WaitAsync(cancellation.Token);
        } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
        } catch (NpgsqlException) {
            Close();
        } finally {
            Close();
        }
    }

    internal Task StreamAsync(CancellationToken cancellationToken) => ChangeStreamWriter.RunAsync(context, dirty.Reader, Ready, AdmitAsync, cancellation.Token, cancellationToken);

    async Task<bool> AdmitAsync(bool data, CancellationToken cancellationToken) {
        if (data)
            await MessageChangeAuthority.ReadAsync(context, keys, KeyId, userId, cancellationToken);
        return Volatile.Read(ref closing) == 0 && !(expiresAt <= DateTime.UtcNow);
    }

    void Close() {
        if (Interlocked.Exchange(ref closing, 1) == 0) {
            dirty.Writer.TryComplete();
            stopped.SetResult(cancellation.CancelAsync());
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        Close();
        await initialized.Task;
        await pump;
        if (deadline != null)
            await deadline.DisposeAsync();
        await requestCancellation.DisposeAsync();
        if (connection != null)
            await connection.DisposeAsync();
        owner.Remove(this);
        await (await stopped.Task);
        cancellation.Dispose();
    }
}
