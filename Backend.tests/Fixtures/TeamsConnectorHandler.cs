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

    /// <summary>The captured native HTTP methods.</summary>
    public ConcurrentQueue<HttpMethod> Methods { get; } = new();

    /// <summary>Whether the SDK supplied a nonempty bearer authorization header.</summary>
    public bool HasBearerAuthorization { get; private set; }

    /// <summary>The optional original exception thrown at the synthetic Connector boundary.</summary>
    public Exception? Failure { get; set; }

    /// <summary>The Connector response status.</summary>
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    /// <summary>The synthetic Connector response content.</summary>
    public string ResponseBody { get; set; } = "{\"id\":\"setup-reply\"}";

    /// <summary>The synthetic raw authentication challenge values.</summary>
    public string[] AuthenticationChallenges { get; set; } = [];

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
        Methods.Enqueue(request.Method);
        if (request.Content != null)
            Bodies.Enqueue(await request.Content.ReadAsStringAsync(cancellationToken));
        Started.TrySetResult();
        if (Failure != null)
            throw Failure;
        if (ResponseBarrier != null)
            await ResponseBarrier.WaitAsync(cancellationToken);
        if (CancelSend)
            throw new OperationCanceledException(cancellationToken);
        HttpResponseMessage response = new(Status) {
            Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json")
        };
        response.Headers.TryAddWithoutValidation("WWW-Authenticate", AuthenticationChallenges);
        return response;
    }
}
