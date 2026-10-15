using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Pooshit.Http;

namespace Backend.Services.Embeddings;

/// <summary>
/// keeps the pinned model files in a directory, downloading the ones which are missing
/// </summary>
public class EmbeddingModelFiles(IHttpService http, ILogger<EmbeddingModelFiles> logger, string directory, EmbeddingModelFile[] files) : IEmbeddingModelFiles {
    const int MaxRedirects = 5;
    const int BufferSize = 1 << 20;

    /// <inheritdoc />
    public async Task EnsureAsync(CancellationToken cancellationToken) {
        Directory.CreateDirectory(directory);
        foreach (EmbeddingModelFile file in files) {
            string path = Path.Combine(directory, file.Name);
            if (File.Exists(path))
                await Verify(file, path, cancellationToken);
            else
                await Download(file, path, cancellationToken);
        }
    }

    async Task Verify(EmbeddingModelFile file, string path, CancellationToken cancellationToken) {
        await using FileStream stream = File.OpenRead(path);
        string actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        if (actual != file.Sha256)
            throw new InvalidOperationException($"embedding model file {path} does not match the pinned sha256 {file.Sha256} (found {actual}). delete the file to download it again.");

        logger.LogInformation("event=embedding.model.verified file={File}", file.Name);
    }

    async Task Download(EmbeddingModelFile file, string path, CancellationToken cancellationToken) {
        string temporary = $"{path}.{Guid.NewGuid():N}.download";
        try {
            Stopwatch watch = Stopwatch.StartNew();
            string actual = await Fetch(file, path, temporary, cancellationToken);
            if (actual != file.Sha256)
                throw new InvalidOperationException($"the download of {file.Url} does not match the pinned sha256 {file.Sha256} (found {actual}). nothing was stored.");

            File.Move(temporary, path, true);
            logger.LogInformation("event=embedding.model.download file={File} bytes={Bytes} duration_ms={Duration}", file.Name, new FileInfo(path).Length, watch.ElapsedMilliseconds);
        } finally {
            File.Delete(temporary);
        }
    }

    async Task<string> Fetch(EmbeddingModelFile file, string path, string temporary, CancellationToken cancellationToken) {
        try {
            string url = file.Url;
            for (int redirects = 0; redirects <= MaxRedirects; redirects++) {
                using HttpResponseMessage response = await http.Get<HttpResponseMessage>(url, null);
                Uri location = response.Headers.Location;
                if (location != null) {
                    url = location.IsAbsoluteUri ? location.OriginalString : new Uri(new Uri(url), location).AbsoluteUri;
                    continue;
                }

                response.EnsureSuccessStatusCode();
                await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await Save(source, temporary, cancellationToken);
            }

            throw new HttpRequestException($"more than {MaxRedirects} redirects");
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            throw new InvalidOperationException($"downloading {file.Url} to {path} failed: {ex.Message}. place the file there by hand, see the Embeddings section of the README.", ex);
        }
    }

    static async Task<string> Save(Stream source, string path, CancellationToken cancellationToken) {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using FileStream target = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, true);
        byte[] buffer = new byte[BufferSize];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0) {
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
