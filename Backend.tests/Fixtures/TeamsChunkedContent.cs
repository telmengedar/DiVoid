using System.Net;
using System.Text;

namespace Backend.tests.Fixtures;

/// <summary>Supplies an unknown-length request body to the in-process webhook.</summary>
public sealed class TeamsChunkedContent : HttpContent {
    readonly byte[] bytes;

    /// <summary>Creates unknown-length request content.</summary>
    public TeamsChunkedContent(string body) {
        bytes = Encoding.UTF8.GetBytes(body);
        Headers.ContentType = new("application/json");
    }

    /// <inheritdoc />
    protected override bool TryComputeLength(out long length) {
        length = 0;
        return false;
    }

    /// <inheritdoc />
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
}
