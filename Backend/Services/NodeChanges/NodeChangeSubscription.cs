using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Backend.Services.ChangeStreams;
using Backend.Models.Nodes;

namespace Backend.Services.NodeChanges;

internal sealed class NodeChangeSubscription : IAsyncDisposable {
    const string Ready = "event:ready\ndata:{\"version\":1,\"coverage\":\"this-process\",\"replay\":false,\"resyncRequired\":true}\n\n";

    readonly NodeChangeFeed owner;
    readonly Channel<bool> dirty = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) {
        FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, AllowSynchronousContinuations = false
    });
    readonly CancellationTokenSource cancellation = new();
    readonly ChangeStreamDeadline deadline;
    readonly TaskCompletionSource<Task> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile bool closed;
    int cancellationStarted;
    bool disposed;

    internal NodeChangeSubscription(NodeChangeFeed owner, long keyId, long userId, bool admin, DateTime? expiresAt, long? organizationId, NodeChangeFilter filter) {
        this.owner = owner;
        KeyId = keyId;
        UserId = userId;
        Admin = admin;
        OrganizationId = organizationId;
        ExpiresAt = expiresAt;
        Filter = filter;
        deadline = new ChangeStreamDeadline(expiresAt, () => Closed, () => _ = owner.RemoveAsync(this));
    }

    internal long KeyId { get; }

    internal long UserId { get; }

    internal bool Admin { get; }

    internal long? OrganizationId { get; }

    internal DateTime? ExpiresAt { get; }

    internal NodeChangeFilter Filter { get; }

    internal bool Closed => closed;

    internal CancellationToken Cancellation => cancellation.Token;

    internal ChannelReader<bool> Reader => dirty.Reader;

    internal bool Expired => ExpiresAt.HasValue && ExpiresAt.Value <= DateTime.UtcNow;

    internal void ArmExpiry() => deadline.Arm();

    void Expire() => deadline.Tick();

    internal bool Selects(NodeChangeFact fact) => fact != null && fact.Readable(UserId, Admin, OrganizationId) &&
        ((fact.Id == Filter.RootNodeId && Filter.Id.Contains(fact.Id)) ||
         (fact.RootNodeId == Filter.RootNodeId && (Filter.Id.Length == 0 && Filter.Type.Length == 0 || Filter.Id.Contains(fact.Id) || Filter.Type.Contains(fact.Type, StringComparer.Ordinal))));

    internal void Apply(NodeChangeDelta delta) {
        NodeChangeFact before = delta.Before;
        NodeChangeFact after = delta.After;
        long id = before?.Id ?? after.Id;
        bool anchor = id == Filter.RootNodeId || Filter.Id.Contains(id);
        if (anchor && (after == null || !after.Readable(UserId, Admin, OrganizationId) || id != Filter.RootNodeId && after.RootNodeId != Filter.RootNodeId)) {
            Close();
            return;
        }
        bool selectedBefore = Selects(before);
        bool selectedAfter = Selects(after);
        if ((selectedBefore || selectedAfter) && (selectedBefore != selectedAfter || (delta.Events & Filter.Events) != 0))
            dirty.Writer.TryWrite(true);
    }

    internal void Close() {
        closed = true;
        dirty.Writer.TryComplete();
    }

    internal void Cancel() {
        if (Interlocked.Exchange(ref cancellationStarted, 1) == 0)
            stopped.SetResult(cancellation.CancelAsync());
    }

    internal Task StreamAsync(HttpContext context, CancellationToken cancellationToken) =>
        ChangeStreamWriter.RunAsync(context, Reader, Ready, (_, token) => owner.AdmitAsync(this, token), Cancellation, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (disposed)
            return;
        disposed = true;
        await owner.RemoveAsync(this);
        await deadline.DisposeAsync();
        await (await stopped.Task);
        cancellation.Dispose();
    }
}
