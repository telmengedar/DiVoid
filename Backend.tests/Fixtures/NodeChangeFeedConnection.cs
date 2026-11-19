using Pooshit.Http;

namespace Backend.tests.Fixtures;

internal sealed class NodeChangeFeedConnection(NodeChangeRequestHandler handler, HttpResponseMessage response, CancellationTokenSource cancellation) : IAsyncDisposable {
    readonly StreamReader reader = new(response.Content.ReadAsStream());

    internal HttpResponseMessage Response => response;

    internal static Task<NodeChangeFeedConnection> OpenAsync(JwtAuthFixture host, string path, string token, CancellationToken cancellationToken = default) =>
        OpenAsync(host.CreateHandler(), path, token, cancellationToken);

    internal static async Task<NodeChangeFeedConnection> OpenAsync(HttpMessageHandler transport, string path, string token, CancellationToken cancellationToken = default) {
        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(60));
        NodeChangeRequestHandler handler = new(transport, cancellation.Token);
        IHttpService http = new HttpService(handler) { Timeout = TimeSpan.FromSeconds(30) };
        HttpResponseMessage? response = null;
        try {
            response = await http.Get<HttpResponseMessage>(path, new HttpOptions {
                CompletionOption = HttpCompletionOption.ResponseHeadersRead, TokenProvider = new NodeChangeTokenProvider(token)
            });
            Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK));
            return new NodeChangeFeedConnection(handler, response, cancellation);
        } catch {
            await cancellation.CancelAsync();
            response?.Dispose();
            handler.Dispose();
            cancellation.Dispose();
            throw;
        }
    }

    internal async Task<string> Frame(int seconds = 5) {
        List<string> lines = [];
        while (true) {
            string? line = await reader.ReadLineAsync(cancellation.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(seconds));
            if (line == null)
                return "<eof>";
            if (line.Length == 0)
                return string.Join('\n', lines);
            lines.Add(line);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        await cancellation.CancelAsync();
        reader.Dispose();
        response.Dispose();
        handler.Dispose();
        cancellation.Dispose();
    }
}
