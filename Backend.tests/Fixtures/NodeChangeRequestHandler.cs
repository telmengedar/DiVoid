namespace Backend.tests.Fixtures;

internal sealed class NodeChangeRequestHandler(HttpMessageHandler inner, CancellationToken lifetime) : DelegatingHandler(inner) {
    CancellationTokenSource? requestCancellation;

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        return base.SendAsync(request, requestCancellation.Token);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing) {
        if (disposing) {
            requestCancellation?.Cancel();
            requestCancellation?.Dispose();
        }
        base.Dispose(disposing);
    }
}
