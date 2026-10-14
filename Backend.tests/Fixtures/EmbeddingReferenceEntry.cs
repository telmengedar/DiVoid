namespace Backend.tests.Fixtures;

/// <summary>
/// one prompt with the token ids and the vector the sentence-transformers reference computed for it
/// </summary>
public class EmbeddingReferenceEntry {
    /// <summary>
    /// name of the text the prompt was built from
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// <c>document</c> or <c>query</c>
    /// </summary>
    public string Purpose { get; set; } = "";

    /// <summary>
    /// text including its task prefix
    /// </summary>
    public string Prompt { get; set; } = "";

    /// <summary>
    /// token ids the reference model received
    /// </summary>
    public int[] InputIds { get; set; } = [];

    /// <summary>
    /// unit vector the reference model returned
    /// </summary>
    public float[] Vector { get; set; } = [];
}
