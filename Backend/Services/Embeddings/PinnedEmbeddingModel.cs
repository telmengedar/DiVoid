namespace Backend.Services.Embeddings;

/// <summary>
/// the files of the EmbeddingGemma 2 model at the Hugging Face revisions this build was verified against
/// </summary>
public static class PinnedEmbeddingModel {

    /// <summary>
    /// name of the ONNX graph file
    /// </summary>
    public const string GraphFile = "model_quantized.onnx";

    /// <summary>
    /// name of the tokenizer definition file
    /// </summary>
    public const string TokenizerFile = "tokenizer.json";

    const string OnnxRepository = "https://huggingface.co/onnx-community/embeddinggemma-2-ONNX/resolve/daa72c51243991dfcaf9f9137d2c573d8f7790c0/onnx/";
    const string TokenizerRepository = "https://huggingface.co/google/embeddinggemma-2/resolve/914f7f89142e33e77833254d9c9b90c3cef7303b/";

    /// <summary>
    /// all files which have to sit side by side in the model directory
    /// </summary>
    public static readonly EmbeddingModelFile[] Files = [
        new(GraphFile, OnnxRepository + GraphFile, "d06edd601f851c633a2519304cbeb8dc6170d7ceb61b436625c17fb9b6e74953"),
        new(GraphFile + "_data", OnnxRepository + GraphFile + "_data", "278a7ff1248c3618e4bd11a607fc54f7bdc7778854230f3956d3f86bd9db4f3b"),
        new(TokenizerFile, TokenizerRepository + TokenizerFile, "4d777ef5bdc1aa36227abdfb77c3e49e7b9c892d16e1b6bda41c393504828be4")
    ];
}
