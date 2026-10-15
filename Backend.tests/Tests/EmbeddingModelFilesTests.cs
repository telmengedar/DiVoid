using System.Net;
using System.Security.Cryptography;
using System.Text;
using Backend.Services.Embeddings;
using Backend.tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Pooshit.Http;

namespace Backend.tests.Tests;

/// <summary>
/// pins how the model files are verified, downloaded and refused, against a faked HTTP service and small files
/// </summary>
[TestFixture, Parallelizable]
public class EmbeddingModelFilesTests {
    const string FirstUrl = "https://models.test/first.bin";
    const string SecondUrl = "https://models.test/second.bin";

    static string Sha256Of(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    static EmbeddingModelFile File(string name, string url, string content) => new(name, url, Sha256Of(content));

    static HttpResponseMessage Ok(string content) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(content)) };

    static HttpResponseMessage RedirectTo(string location) {
        HttpResponseMessage response = new(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    static void Serve(Mock<IHttpService> http, string url, Func<HttpResponseMessage> response) {
        http.Setup(h => h.Get<HttpResponseMessage>(url, It.IsAny<HttpOptions>())).ReturnsAsync(response);
    }

    static EmbeddingModelFiles FilesOf(Mock<IHttpService> http, string directory, params EmbeddingModelFile[] files) {
        return new EmbeddingModelFiles(http.Object, NullLogger<EmbeddingModelFiles>.Instance, directory, files);
    }

    static void Place(TemporaryDirectory directory, string name, string content) {
        System.IO.File.WriteAllText(Path.Combine(directory.Path, name), content);
    }


    [Test, Parallelizable]
    [Description("A missing file is downloaded, verified against its hash and left under its own name.")]
    public async Task EnsureAsync_FileAbsent_DownloadsAndVerifies() {
        using TemporaryDirectory directory = new();
        Mock<IHttpService> http = new();
        Serve(http, FirstUrl, () => Ok("first content"));

        await FilesOf(http, directory.Path, File("first.bin", FirstUrl, "first content")).EnsureAsync(CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(System.IO.File.ReadAllText(Path.Combine(directory.Path, "first.bin")), Is.EqualTo("first content"));
            Assert.That(Directory.GetFileSystemEntries(directory.Path), Has.Length.EqualTo(1));
        });
    }


    [Test, Parallelizable]
    [Description("A download is followed through a redirect, as the Hugging Face file URLs answer with one.")]
    public async Task EnsureAsync_Redirect_DownloadsFromLocation() {
        using TemporaryDirectory directory = new();
        Mock<IHttpService> http = new();
        Serve(http, FirstUrl, () => RedirectTo("https://cdn.test/first-stored.bin"));
        Serve(http, "https://cdn.test/first-stored.bin", () => Ok("first content"));

        await FilesOf(http, directory.Path, File("first.bin", FirstUrl, "first content")).EnsureAsync(CancellationToken.None);

        Assert.That(System.IO.File.ReadAllText(Path.Combine(directory.Path, "first.bin")), Is.EqualTo("first content"));
    }


    [Test, Parallelizable]
    [Description("The signed location of a redirect is requested with its escapes intact, because the signature is computed over them.")]
    public async Task EnsureAsync_RedirectWithEscapedSignature_RequestsLocationUnchanged() {
        using TemporaryDirectory directory = new();
        Mock<IHttpService> http = new();
        const string signed = "https://cdn.test/stored/first.bin?name=a%3Bb&Signature=MEUC%7Ee107%7E5&Key=1";
        Serve(http, FirstUrl, () => RedirectTo(signed));
        Serve(http, signed, () => Ok("first content"));

        await FilesOf(http, directory.Path, File("first.bin", FirstUrl, "first content")).EnsureAsync(CancellationToken.None);

        Assert.That(System.IO.File.ReadAllText(Path.Combine(directory.Path, "first.bin")), Is.EqualTo("first content"));
    }


    [Test, Parallelizable]
    [Description("A redirect to a relative location is resolved against the address which answered.")]
    public async Task EnsureAsync_RelativeRedirect_ResolvesAgainstRequestedUrl() {
        using TemporaryDirectory directory = new();
        Mock<IHttpService> http = new();
        HttpResponseMessage redirect = new(HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("/stored/first.bin", UriKind.Relative);
        Serve(http, FirstUrl, () => redirect);
        Serve(http, "https://models.test/stored/first.bin", () => Ok("first content"));

        await FilesOf(http, directory.Path, File("first.bin", FirstUrl, "first content")).EnsureAsync(CancellationToken.None);

        Assert.That(System.IO.File.ReadAllText(Path.Combine(directory.Path, "first.bin")), Is.EqualTo("first content"));
    }


    [Test, Parallelizable]
    [Description("A redirect loop ends in a refusal naming the download instead of looping forever.")]
    public void EnsureAsync_RedirectLoop_ThrowsNamingUrl() {
        using TemporaryDirectory directory = new();
        Mock<IHttpService> http = new();
        Serve(http, FirstUrl, () => RedirectTo(FirstUrl));

        InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(() => FilesOf(http, directory.Path, File("first.bin", FirstUrl, "first content")).EnsureAsync(CancellationToken.None));

        Assert.Multiple(() => {
            Assert.That(exception!.Message, Does.Contain(FirstUrl));
            Assert.That(exception.Message, Does.Contain("redirects"));
            http.Verify(h => h.Get<HttpResponseMessage>(FirstUrl, It.IsAny<HttpOptions>()), Times.Exactly(6));
        });
    }


    [Test, Parallelizable]
    [Description("A download which does not hash to the pinned value is refused and leaves no file behind, not even a temporary one.")]
    public void EnsureAsync_HashMismatchOnDownload_ThrowsAndLeavesNoFile() {
        using TemporaryDirectory directory = new();
        Mock<IHttpService> http = new();
        Serve(http, FirstUrl, () => Ok("somebody else's content"));
        EmbeddingModelFile pinned = File("first.bin", FirstUrl, "first content");

        InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(() => FilesOf(http, directory.Path, pinned).EnsureAsync(CancellationToken.None));

        Assert.Multiple(() => {
            Assert.That(exception!.Message, Does.Contain(pinned.Sha256));
            Assert.That(exception.Message, Does.Contain(FirstUrl));
            Assert.That(Directory.GetFileSystemEntries(directory.Path), Is.Empty);
        });
    }


    [Test, Parallelizable]
    [Description("A file which is present but does not match its hash is never overwritten; startup refuses and names the remedy.")]
    public void EnsureAsync_PresentWithOtherHash_ThrowsKeepsFileAndMakesNoNetworkCall() {
        using TemporaryDirectory directory = new();
        Place(directory, "first.bin", "placed by the operator");
        Mock<IHttpService> http = new();
        EmbeddingModelFile pinned = File("first.bin", FirstUrl, "first content");

        InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(() => FilesOf(http, directory.Path, pinned).EnsureAsync(CancellationToken.None));

        Assert.Multiple(() => {
            Assert.That(exception!.Message, Does.Contain(Path.Combine(directory.Path, "first.bin")));
            Assert.That(exception.Message, Does.Contain(pinned.Sha256));
            Assert.That(exception.Message, Does.Contain("delete the file"));
            Assert.That(System.IO.File.ReadAllText(Path.Combine(directory.Path, "first.bin")), Is.EqualTo("placed by the operator"));
            http.VerifyNoOtherCalls();
        });
    }


    [Test, Parallelizable]
    [Description("Files which are present and match are used as they are, without any network call.")]
    public async Task EnsureAsync_PresentWithPinnedHash_MakesNoNetworkCall() {
        using TemporaryDirectory directory = new();
        Place(directory, "first.bin", "first content");
        Mock<IHttpService> http = new();

        await FilesOf(http, directory.Path, File("first.bin", FirstUrl, "first content")).EnsureAsync(CancellationToken.None);

        http.VerifyNoOtherCalls();
    }


    [Test, Parallelizable]
    [Description("Only the files which are missing are fetched, so a restart after a partial download resumes at the next file.")]
    public async Task EnsureAsync_OneFilePresent_DownloadsOnlyTheMissingOne() {
        using TemporaryDirectory directory = new();
        Place(directory, "first.bin", "first content");
        Mock<IHttpService> http = new();
        Serve(http, SecondUrl, () => Ok("second content"));

        await FilesOf(http, directory.Path, File("first.bin", FirstUrl, "first content"), File("second.bin", SecondUrl, "second content")).EnsureAsync(CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(System.IO.File.ReadAllText(Path.Combine(directory.Path, "second.bin")), Is.EqualTo("second content"));
            http.Verify(h => h.Get<HttpResponseMessage>(FirstUrl, It.IsAny<HttpOptions>()), Times.Never);
        });
    }


    [Test, Parallelizable]
    [Description("A file which another instance placed while this one downloaded is replaced by the verified download, as both are identical.")]
    public async Task EnsureAsync_FilePlacedDuringDownload_KeepsVerifiedFile() {
        using TemporaryDirectory directory = new();
        Mock<IHttpService> http = new();
        Serve(http, FirstUrl, () => {
            Place(directory, "first.bin", "first content");
            return Ok("first content");
        });

        await FilesOf(http, directory.Path, File("first.bin", FirstUrl, "first content")).EnsureAsync(CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(System.IO.File.ReadAllText(Path.Combine(directory.Path, "first.bin")), Is.EqualTo("first content"));
            Assert.That(Directory.GetFileSystemEntries(directory.Path), Has.Length.EqualTo(1));
        });
    }


    [Test, Parallelizable]
    [Description("A missing model directory is created for the download.")]
    public async Task EnsureAsync_DirectoryMissing_CreatesItAndDownloads() {
        using TemporaryDirectory directory = new(false);
        Mock<IHttpService> http = new();
        Serve(http, FirstUrl, () => Ok("first content"));

        await FilesOf(http, Path.Combine(directory.Path, "models", "gemma"), File("first.bin", FirstUrl, "first content")).EnsureAsync(CancellationToken.None);

        Assert.That(System.IO.File.Exists(Path.Combine(directory.Path, "models", "gemma", "first.bin")), Is.True);
    }


    [Test, Parallelizable]
    [Description("A failing connection refuses startup with the URL, the target path and the README section, and keeps the cause.")]
    public void EnsureAsync_ConnectionFails_ThrowsNamingUrlPathAndReadme() {
        using TemporaryDirectory directory = new();
        Mock<IHttpService> http = new();
        http.Setup(h => h.Get<HttpResponseMessage>(FirstUrl, It.IsAny<HttpOptions>())).ThrowsAsync(new HttpRequestException("name not resolved"));

        InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(() => FilesOf(http, directory.Path, File("first.bin", FirstUrl, "first content")).EnsureAsync(CancellationToken.None));

        Assert.Multiple(() => {
            Assert.That(exception!.Message, Does.Contain(FirstUrl));
            Assert.That(exception.Message, Does.Contain(Path.Combine(directory.Path, "first.bin")));
            Assert.That(exception.Message, Does.Contain("README"));
            Assert.That(exception.Message, Does.Contain("name not resolved"));
            Assert.That(exception.InnerException, Is.TypeOf<HttpRequestException>());
            Assert.That(Directory.GetFileSystemEntries(directory.Path), Is.Empty);
        });
    }


    [Test, Parallelizable]
    [Description("An HTTP error status is a failed download, not an empty file.")]
    public void EnsureAsync_NotFound_ThrowsNamingStatusAndLeavesNoFile() {
        using TemporaryDirectory directory = new();
        Mock<IHttpService> http = new();
        Serve(http, FirstUrl, () => new HttpResponseMessage(HttpStatusCode.NotFound));

        InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(() => FilesOf(http, directory.Path, File("first.bin", FirstUrl, "first content")).EnsureAsync(CancellationToken.None));

        Assert.Multiple(() => {
            Assert.That(exception!.Message, Does.Contain("404"));
            Assert.That(Directory.GetFileSystemEntries(directory.Path), Is.Empty);
        });
    }


    [Test, Parallelizable]
    [Description("A token cancelled while the answer is being copied ends the download with a cancellation and leaves no file.")]
    public void EnsureAsync_CancelledDuringDownload_ThrowsCancellationAndLeavesNoFile() {
        using TemporaryDirectory directory = new();
        using CancellationTokenSource source = new();
        Mock<IHttpService> http = new();
        Serve(http, FirstUrl, () => {
            source.Cancel();
            return Ok("first content");
        });

        Assert.ThrowsAsync<TaskCanceledException>(() => FilesOf(http, directory.Path, File("first.bin", FirstUrl, "first content")).EnsureAsync(source.Token));

        Assert.That(Directory.GetFileSystemEntries(directory.Path), Is.Empty);
    }


    [Test, Parallelizable]
    [Description("Cancelling the startup surfaces as a cancellation, not as a failed download.")]
    public void EnsureAsync_Cancelled_PropagatesCancellation() {
        using TemporaryDirectory directory = new();
        Mock<IHttpService> http = new();
        http.Setup(h => h.Get<HttpResponseMessage>(FirstUrl, It.IsAny<HttpOptions>())).ThrowsAsync(new OperationCanceledException());

        Assert.ThrowsAsync<OperationCanceledException>(() => FilesOf(http, directory.Path, File("first.bin", FirstUrl, "first content")).EnsureAsync(CancellationToken.None));
    }


    [Test, Parallelizable]
    [Description("The pinned files are the graph, its external data and the tokenizer, fetched over HTTPS from Hugging Face at a fixed revision.")]
    public void Files_Pinned_AreHttpsRevisionAddressedAndHashed() {
        EmbeddingModelFile[] files = PinnedEmbeddingModel.Files;

        Assert.Multiple(() => {
            Assert.That(files.Select(file => file.Name), Is.EqualTo(new[] { "model_quantized.onnx", "model_quantized.onnx_data", "tokenizer.json" }));
            Assert.That(files.Select(file => file.Url), Has.All.StartWith("https://huggingface.co/").And.All.Match(@"^https://huggingface\.co/[^/]+/[^/]+/resolve/[0-9a-f]{40}/.+"));
            Assert.That(files.Select(file => file.Sha256), Has.All.Match("^[0-9a-f]{64}$"));
            Assert.That(files.Select(file => file.Url.Split('/').Last()), Is.EqualTo(files.Select(file => file.Name)));
        });
    }
}
