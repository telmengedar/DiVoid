using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Backend.tests.Fixtures;

/// <summary>Captures native Teams SDK Connector requests without external I/O.</summary>
public sealed class TeamsConnectorHandler : HttpMessageHandler {
    /// <summary>The SDK request payloads.</summary>
    public ConcurrentQueue<string> Bodies { get; } = new();

    /// <summary>The SDK request destinations.</summary>
    public ConcurrentQueue<Uri> Destinations { get; } = new();

    /// <summary>Whether the SDK supplied a nonempty bearer authorization header.</summary>
    public bool HasBearerAuthorization { get; private set; }

    /// <summary>The Connector response status.</summary>
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    /// <summary>The first observed outbound request.</summary>
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The optional release barrier for an outbound response.</summary>
    public Task? ResponseBarrier { get; set; }

    /// <summary>Whether the transport reports cancellation.</summary>
    public bool CancelSend { get; set; }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        HasBearerAuthorization = request.Headers.Authorization is { Scheme: "Bearer", Parameter.Length: > 0 };
        Destinations.Enqueue(request.RequestUri!);
        Bodies.Enqueue(await request.Content!.ReadAsStringAsync(cancellationToken));
        Started.TrySetResult();
        if (ResponseBarrier != null)
            await ResponseBarrier.WaitAsync(cancellationToken);
        if (CancelSend)
            throw new OperationCanceledException(cancellationToken);
        return new HttpResponseMessage(Status) {
            Content = new StringContent("{\"id\":\"setup-reply\"}", Encoding.UTF8, "application/json")
        };
    }
}
