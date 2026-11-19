using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Backend.tests.Fixtures;

/// <summary>Pauses an actual SSE response write without blocking its committed producer.</summary>
public sealed class NodeChangeWriteBarrier : IStartupFilter {
    internal volatile bool Armed;

    internal bool BeforeHeaders { get; init; }

    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
        app.Use(async (context, invoke) => {
            try {
                if (context.Request.Path == "/api/v1/nodechanges") {
                    if (BeforeHeaders) {
                        Entered.TrySetResult();
                        await Release.Task.WaitAsync(context.RequestAborted);
                    }
                    context.Response.Body = new NodeChangePausingStream(context.Response.Body, this);
                }
                await invoke();
            } finally {
                Completed.TrySetResult();
            }
        });
        next(app);
    };
}
