namespace Backend.Services.Embeddings;

/// <summary>
/// thrown when a caller requests semantic search (e.g. <c>?query=...</c> or
/// <c>?minSimilarity=...</c>) on a deployment that does not support the
/// <c>embedding()</c> database function.
///
/// This is a narrowly-scoped client-parameter error — it does not remap every
/// <see cref="System.InvalidOperationException"/> in the codebase.  The
/// dedicated <see cref="Errors.SemanticSearchUnavailableExceptionHandler"/>
/// produces HTTP 400 <c>badparameter</c> via the standard error pipeline.
/// </summary>
public class SemanticSearchUnavailableException : Exception
{
    /// <summary>
    /// creates a new <see cref="SemanticSearchUnavailableException"/>
    /// </summary>
    /// <param name="message">human-readable explanation for the caller</param>
    public SemanticSearchUnavailableException(string message)
        : base(message)
    {
    }
}
