namespace Backend.Services.Embeddings;

/// <summary>
/// keeps a database from holding vectors of two embedding models
/// </summary>
public interface IEmbeddingModelGuard {

    /// <summary>
    /// records <paramref name="model"/> for a database without vectors and refuses a database whose vectors come from another model.
    /// does nothing while the embedding capability is disabled.
    /// </summary>
    /// <param name="model">model identity of the configured producer</param>
    /// <exception cref="InvalidOperationException">the database holds vectors of another model</exception>
    Task EnsureAsync(string model);
}
