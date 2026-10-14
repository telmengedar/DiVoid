namespace Backend.Services.Embeddings;

/// <summary>
/// provides the files of the local embedding model
/// </summary>
public interface IEmbeddingModelFiles {

    /// <summary>
    /// downloads the files which are missing and verifies all files against their pinned hashes
    /// </summary>
    /// <param name="cancellationToken">cancels the download</param>
    /// <exception cref="InvalidOperationException">a download failed, or a file does not match its pinned hash</exception>
    Task EnsureAsync(CancellationToken cancellationToken);
}
