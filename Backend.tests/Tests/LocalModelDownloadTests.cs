using System.Security.Cryptography;
using Backend.Services.Embeddings;
using Backend.tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Pooshit.Http;

namespace Backend.tests.Tests;

/// <summary>
/// LocalModel lane: downloads a pinned file from Hugging Face through the real HTTP service
/// </summary>
[TestFixture, Parallelizable]
public class LocalModelDownloadTests {
    [Test, Parallelizable]
    [Explicit("LocalModel lane - requires network access to huggingface.co; runs on the gangolf Docker daemon only.")]
    [Category("LocalModel")]
    [Description("The pinned tokenizer file is fetched through the Hugging Face redirect and matches its pinned hash, and a second run downloads nothing.")]
    public async Task EnsureAsync_RealHuggingFace_DownloadsPinnedTokenizerOnce() {
        using TemporaryDirectory directory = new();
        EmbeddingModelFile tokenizer = PinnedEmbeddingModel.Files.Single(file => file.Name == PinnedEmbeddingModel.TokenizerFile);
        EmbeddingModelFiles files = new(new HttpService { Timeout = TimeSpan.FromMinutes(10) }, NullLogger<EmbeddingModelFiles>.Instance, directory.Path, [tokenizer]);

        await files.EnsureAsync(CancellationToken.None);
        DateTime firstWrite = File.GetLastWriteTimeUtc(Path.Combine(directory.Path, tokenizer.Name));
        await files.EnsureAsync(CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory.Path, tokenizer.Name)))), Is.EqualTo(tokenizer.Sha256));
            Assert.That(File.GetLastWriteTimeUtc(Path.Combine(directory.Path, tokenizer.Name)), Is.EqualTo(firstWrite));
            Assert.That(Directory.GetFileSystemEntries(directory.Path), Has.Length.EqualTo(1));
        });
    }
}
