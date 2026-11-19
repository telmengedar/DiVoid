using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Backend.Models.Nodes;

namespace Backend.Services.NodeChanges;

internal sealed class NodeChangeSubscription : IAsyncDisposable {
    const string Ready = "event:ready\ndata:{\"version\":1,\"coverage\":\"this-process\",\"replay\":false,\"resyncRequired\":true}\n\n";
    const string Invalidate = "event:invalidate\ndata:{}\n\n";

    readonly NodeChangeFeed owner;
    readonly Channel<bool> dirty = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) {
        FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, AllowSynchronousContinuations = false
    });
    readonly CancellationTokenSource cancellation = new();
    readonly Timer expiryTimer;
    readonly TaskCompletionSource<Task> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile bool closed;
    int cancellationStarted;
    bool disposed;

    internal NodeChangeSubscription(NodeChangeFeed owner, long keyId, long userId, bool admin, DateTime? expiresAt, NodeChangeFilter filter) {
        this.owner = owner;
        KeyId = keyId;
        UserId = userId;
        Admin = admin;
        ExpiresAt = expiresAt;
        Filter = filter;
        if (expiresAt.HasValue)
            expiryTimer = new Timer(_ => Expire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal long KeyId { get; }

    internal long UserId { get; }

    internal bool Admin { get; }

    internal DateTime? ExpiresAt { get; }

    internal NodeChangeFilter Filter { get; }

    internal bool Closed => closed;

    internal CancellationToken Cancellation => cancellation.Token;

    internal ChannelReader<bool> Reader => dirty.Reader;

    internal bool Expired => ExpiresAt.HasValue && ExpiresAt.Value <= DateTime.UtcNow;

    internal void ArmExpiry() {
        if (!ExpiresAt.HasValue || Closed)
            return;
        TimeSpan remaining = ExpiresAt.Value - DateTime.UtcNow;
        try {
            expiryTimer.Change(remaining <= TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Math.Min(remaining.TotalMilliseconds, uint.MaxValue - 1d)), Timeout.InfiniteTimeSpan);
        } catch (ObjectDisposedException) when (Closed) {
        }
    }

    void Expire() {
        if (Expired)
            _ = owner.RemoveAsync(this);
        else
            ArmExpiry();
    }

    internal bool Selects(NodeChangeFact fact) => fact != null && fact.Readable(UserId, Admin) &&
        ((fact.Id == Filter.RootNodeId && Filter.Id.Contains(fact.Id)) ||
         (fact.RootNodeId == Filter.RootNodeId && (Filter.Id.Length == 0 && Filter.Type.Length == 0 || Filter.Id.Contains(fact.Id) || Filter.Type.Contains(fact.Type, StringComparer.Ordinal))));

    internal void Apply(NodeChangeDelta delta) {
        NodeChangeFact before = delta.Before;
        NodeChangeFact after = delta.After;
        long id = before?.Id ?? after.Id;
        bool anchor = id == Filter.RootNodeId || Filter.Id.Contains(id);
        if (anchor && (after == null || !after.Readable(UserId, Admin) || id != Filter.RootNodeId && after.RootNodeId != Filter.RootNodeId)) {
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

    internal async Task StreamAsync(HttpContext context, CancellationToken cancellationToken) {
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Cancellation);
        try {
            if (!await WriteAsync(context.Response, Ready, lifetime.Token))
                return;
            Task<bool> change = Reader.WaitToReadAsync(lifetime.Token).AsTask();
            Task heartbeat = Task.Delay(TimeSpan.FromSeconds(15), lifetime.Token);
            while (!lifetime.IsCancellationRequested) {
                Task completed = await Task.WhenAny(change, heartbeat);
                if (completed == change) {
                    if (!await change)
                        break;
                    if (Reader.TryRead(out bool dirty) && !await WriteAsync(context.Response, Invalidate, lifetime.Token))
                        break;
                    change = Reader.WaitToReadAsync(lifetime.Token).AsTask();
                } else {
                    await heartbeat;
                    if (!await WriteAsync(context.Response, ":keepalive\n\n", lifetime.Token))
                        break;
                    heartbeat = Task.Delay(TimeSpan.FromSeconds(15), lifetime.Token);
                }
            }
        } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) {
        } catch (TimeoutException) {
            context.Abort();
        } finally {
            await lifetime.CancelAsync();
        }
    }

    async Task<bool> WriteAsync(HttpResponse response, string frame, CancellationToken cancellationToken) {
        if (!await owner.AdmitAsync(this, cancellationToken))
            return false;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try {
            await response.WriteAsync(frame, deadline.Token).WaitAsync(deadline.Token);
            await response.Body.FlushAsync(deadline.Token).WaitAsync(deadline.Token);
        } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
            throw new TimeoutException("The change-stream writer did not complete within its bound.");
        }
        return true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (disposed)
            return;
        disposed = true;
        await owner.RemoveAsync(this);
        if (expiryTimer != null)
            await expiryTimer.DisposeAsync();
        await (await stopped.Task);
        cancellation.Dispose();
    }
}
