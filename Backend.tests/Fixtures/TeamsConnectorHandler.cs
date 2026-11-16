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

    /// <summary>The Connector response status.</summary>
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    /// <summary>Whether the transport reports cancellation.</summary>
    public bool CancelSend { get; set; }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        Destinations.Enqueue(request.RequestUri!);
        Bodies.Enqueue(await request.Content!.ReadAsStringAsync(cancellationToken));
        if (CancelSend)
            throw new OperationCanceledException(cancellationToken);
        return new HttpResponseMessage(Status) {
            Content = new StringContent("{\"id\":\"setup-reply\"}", Encoding.UTF8, "application/json")
        };
    }
}
