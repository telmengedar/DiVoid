using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Backend.Services.ChangeStreams;

internal static class ChangeStreamWriter {
    internal static async Task RunAsync(HttpContext context, ChannelReader<bool> reader, string ready,
        Func<bool, CancellationToken, Task<bool>> admit, CancellationToken stopped, CancellationToken request) {
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopped, request);
        try {
            if (!await WriteAsync(context.Response, ready, true, admit, lifetime.Token))
                return;
            Task<bool> change = reader.WaitToReadAsync(lifetime.Token).AsTask();
            Task heartbeat = Task.Delay(TimeSpan.FromSeconds(15), lifetime.Token);
            while (!lifetime.IsCancellationRequested) {
                Task completed = await Task.WhenAny(change, heartbeat);
                if (completed == change) {
                    if (!await change)
                        break;
                    if (reader.TryRead(out bool dirty) && !await WriteAsync(context.Response, "event:invalidate\ndata:{}\n\n", true, admit, lifetime.Token))
                        break;
                    change = reader.WaitToReadAsync(lifetime.Token).AsTask();
                } else {
                    await heartbeat;
                    if (!await WriteAsync(context.Response, ":keepalive\n\n", false, admit, lifetime.Token))
                        break;
                    heartbeat = Task.Delay(TimeSpan.FromSeconds(15), lifetime.Token);
                }
            }
        } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) {
        } catch (TimeoutException) {
            context.Abort();
        } catch (Exception exception) when (context.Response.HasStarted) {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ChangeStreamWriter")
                .LogError(exception, "event=change.stream.failed");
            context.Abort();
        } finally {
            await lifetime.CancelAsync();
        }
    }

    static async Task<bool> WriteAsync(HttpResponse response, string frame, bool data, Func<bool, CancellationToken, Task<bool>> admit, CancellationToken cancellationToken) {
        if (!await admit(data, cancellationToken))
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
}
