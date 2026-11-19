namespace Backend.Services.ChangeStreams;

internal sealed class ChangeStreamDeadline : IAsyncDisposable {
    readonly DateTime? expiresAt;
    readonly Func<bool> closed;
    readonly Action expire;
    readonly Timer timer;
    int disposed;

    internal ChangeStreamDeadline(DateTime? expiresAt, Func<bool> closed, Action expire) {
        this.expiresAt = expiresAt;
        this.closed = closed;
        this.expire = expire;
        if (expiresAt.HasValue)
            timer = new Timer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal void Arm() {
        if (!expiresAt.HasValue || closed() || Volatile.Read(ref disposed) != 0)
            return;
        TimeSpan remaining = expiresAt.Value - DateTime.UtcNow;
        try {
            timer.Change(remaining <= TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Math.Min(remaining.TotalMilliseconds, uint.MaxValue - 1d)), Timeout.InfiniteTimeSpan);
        } catch (ObjectDisposedException) when (closed() || Volatile.Read(ref disposed) != 0) {
        }
    }

    internal void Tick() {
        if (closed() || Volatile.Read(ref disposed) != 0)
            return;
        if (expiresAt <= DateTime.UtcNow)
            expire();
        else
            Arm();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        Interlocked.Exchange(ref disposed, 1);
        if (timer != null)
            await timer.DisposeAsync();
    }
}
