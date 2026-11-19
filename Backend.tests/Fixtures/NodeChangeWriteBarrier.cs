using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Backend.tests.Fixtures;

/// <summary>Pauses an actual SSE response write without blocking its committed producer.</summary>
public sealed class NodeChangeWriteBarrier : IStartupFilter {
    internal volatile bool Armed;
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
        app.Use(async (context, invoke) => {
            if (context.Request.Path == "/api/v1/nodechanges")
                context.Response.Body = new PausingStream(context.Response.Body, this);
            await invoke();
        });
        next(app);
    };

    sealed class PausingStream(Stream inner, NodeChangeWriteBarrier barrier) : Stream {
        /// <inheritdoc />
        public override bool CanRead => false;
        /// <inheritdoc />
        public override bool CanSeek => false;
        /// <inheritdoc />
        public override bool CanWrite => true;
        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();
        /// <inheritdoc />
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        /// <inheritdoc />
        public override void Flush() => inner.Flush();
        /// <inheritdoc />
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        /// <inheritdoc />
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        /// <inheritdoc />
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) {
            if (barrier.Armed) {
                barrier.Entered.TrySetResult();
                await barrier.Release.Task.WaitAsync(cancellationToken);
            }
            await inner.WriteAsync(buffer, cancellationToken);
        }
    }
}
