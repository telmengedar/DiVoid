namespace Backend.Services.Embeddings;

/// <summary>
/// the prompt EmbeddingGemma 2 receives for a text: the task prefix of its purpose in front of the text
/// </summary>
public static class EmbeddingPrompt {

    /// <summary>
    /// prefix of a text which is stored
    /// </summary>
    public const string Document = "title: none | text: ";

    /// <summary>
    /// prefix of a text which is compared against stored texts
    /// </summary>
    public const string Query = "task: search result | query: ";

    static readonly string[] MediaPlaceholders = ["<|image|>", "<|audio|>", "<|video|>"];

    /// <summary>
    /// puts the prefix matching <paramref name="purpose"/> in front of <paramref name="text"/> and breaks up the placeholders of media inputs with a space behind their opening bracket
    /// </summary>
    /// <param name="text">text to prefix</param>
    /// <param name="purpose">what the embedded text is used for</param>
    /// <returns>prompt to embed</returns>
    public static string For(string text, EmbeddingPurpose purpose) {
        string embeddable = MediaPlaceholders.Aggregate(text, (current, placeholder) => current.Replace(placeholder, "< " + placeholder[1..]));
        return (purpose == EmbeddingPurpose.Query ? Query : Document) + embeddable;
    }
}
