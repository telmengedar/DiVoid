namespace Backend.Services.Embeddings;

/// <summary>
/// computes embeddings in this process
/// </summary>
public interface ILocalEmbeddingModel {

    /// <summary>
    /// loads the model, which otherwise happens with the first embedding
    /// </summary>
    void Load();

    /// <summary>
    /// embeds a prompt
    /// </summary>
    /// <param name="prompt">text including its task prefix</param>
    /// <returns>vector of length one with <see cref="EmbeddingExpression.Dimensions"/> components</returns>
    /// <exception cref="InvalidOperationException">the model returned a vector of another shape, or without a usable length</exception>
    float[] Embed(string prompt);
}
