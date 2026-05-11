namespace Backend.Services.Embeddings;

/// <summary>
/// indicates whether the active database supports the server-side embedding() function.
/// registered as a singleton at startup, derived from Database:Type configuration.
/// true only for Postgres deployments where the embedding function is expected to exist.
/// </summary>
public interface IEmbeddingCapability {

    /// <summary>
    /// true when the active database is Postgres and the embedding() custom function
    /// should be called on text-content uploads.
    /// false on SQLite (test fixture) — the embedding step is skipped entirely.
    /// </summary>
    bool IsEnabled { get; }
}
