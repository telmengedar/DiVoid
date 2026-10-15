using Tokenizers.DotNet;

namespace Backend.Services.Embeddings;

/// <summary>
/// the Hugging Face tokenizer of EmbeddingGemma 2, loaded from its <c>tokenizer.json</c>
/// </summary>
public sealed class EmbeddingTokenizer : IDisposable {

    /// <summary>
    /// number of tokens of the context of the model
    /// </summary>
    public const int MaxTokens = 8192;

    readonly Tokenizer tokenizer;

    /// <summary>
    /// creates a new <see cref="EmbeddingTokenizer"/>
    /// </summary>
    /// <param name="path">path to <c>tokenizer.json</c></param>
    public EmbeddingTokenizer(string path) {
        tokenizer = new Tokenizer(path);
    }

    /// <summary>
    /// cuts a token sequence to <see cref="MaxTokens"/>
    /// </summary>
    /// <param name="ids">token ids</param>
    /// <returns>the first <see cref="MaxTokens"/> ids, or all ids when there are fewer</returns>
    public static int[] Truncate(int[] ids) {
        return ids.Length > MaxTokens ? ids[..MaxTokens] : ids;
    }

    /// <summary>
    /// splits a text into token ids, including the begin and end of sequence tokens
    /// </summary>
    /// <param name="text">text to tokenize</param>
    /// <returns>token ids, at most <see cref="MaxTokens"/></returns>
    public int[] Encode(string text) {
        return Truncate(tokenizer.Encode(text).Select(id => (int) id).ToArray());
    }

    /// <inheritdoc />
    public void Dispose() {
        tokenizer.Dispose();
    }
}
