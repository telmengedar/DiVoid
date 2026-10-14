using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Backend.Services.Embeddings;

/// <summary>
/// runs the quantized EmbeddingGemma 2 graph with ONNX Runtime on the CPU
/// </summary>
public sealed class LocalEmbeddingModel : ILocalEmbeddingModel, IDisposable {
    const string OutputName = "sentence_embedding";
    const int MediaFeatureWidth = 512;

    readonly Lazy<EmbeddingTokenizer> tokenizer;
    readonly Lazy<InferenceSession> session;

    /// <summary>
    /// creates a new <see cref="LocalEmbeddingModel"/>, which reads nothing before <see cref="Load"/> or the first <see cref="Embed"/>
    /// </summary>
    /// <param name="directory">directory containing the files of <see cref="PinnedEmbeddingModel"/></param>
    public LocalEmbeddingModel(string directory) {
        tokenizer = new Lazy<EmbeddingTokenizer>(() => new EmbeddingTokenizer(Path.Combine(directory, PinnedEmbeddingModel.TokenizerFile)));
        session = new Lazy<InferenceSession>(() => new InferenceSession(Path.Combine(directory, PinnedEmbeddingModel.GraphFile)));
    }

    /// <inheritdoc />
    public void Load() {
        _ = tokenizer.Value;
        _ = session.Value;
    }

    /// <inheritdoc />
    public float[] Embed(string prompt) {
        long[] ids = tokenizer.Value.Encode(prompt).Select(id => (long) id).ToArray();
        long[] mask = Enumerable.Repeat(1L, ids.Length).ToArray();
        NamedOnnxValue[] inputs = [
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [1, ids.Length])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, [1, ids.Length])),
            NamedOnnxValue.CreateFromTensor("image_features", new DenseTensor<float>(new[] { 0, MediaFeatureWidth })),
            NamedOnnxValue.CreateFromTensor("video_features", new DenseTensor<float>(new[] { 0, MediaFeatureWidth })),
            NamedOnnxValue.CreateFromTensor("audio_features", new DenseTensor<float>(new[] { 0, MediaFeatureWidth }))
        ];

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = session.Value.Run(inputs, [OutputName]);
        Tensor<float> output = results[0].AsTensor<float>();
        return EmbeddingVector.FromModelOutput(output.Dimensions.ToArray(), output.ToArray());
    }

    /// <inheritdoc />
    public void Dispose() {
        if (session.IsValueCreated)
            session.Value.Dispose();
        if (tokenizer.IsValueCreated)
            tokenizer.Value.Dispose();
    }
}
