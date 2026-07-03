using System.Text;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.tests.Tests;

/// <summary>
/// tests for the automatic embedding-on-content-write feature (PR A).
/// all tests run on SQLite (via DatabaseFixture or WebApplicationFactory with Sqlite config).
/// the Postgres embedding() call path is not exercised here — the provider IsEnabled check
/// ensures it is never reached in these tests.
/// </summary>
[TestFixture]
public class EmbeddingTests
{
    static NodeService MakeService(DatabaseFixture fixture)
        => new(fixture.EntityManager, NullEmbeddingProvider.Instance);

    [Test]
    public void EmbeddingProvider_SqliteTestFactory_IsDisabled()
    {
        using WebApplicationFactory<Program> factory = TestSetup.CreateTestFactory("capability_check");
        IEmbeddingProvider provider = factory.Services.GetRequiredService<IEmbeddingProvider>();
        Assert.That(provider.IsEnabled, Is.False,
            "IEmbeddingProvider.IsEnabled must be false when Embedding:Provider is absent (SQLite/dev)");
    }

    [Test]
    public async Task UploadContent_TextType_SqliteFixture_EmbeddingRemainsNull()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await svc.CreateNode(new NodeDetails { Type = "task", Name = "EmbedSkipTest" }, callerId: 0);
        byte[] content = Encoding.UTF8.GetBytes("# hello\nsome markdown content");

        await svc.UploadContent(node.Id, "text/markdown", new MemoryStream(content), callerId: 0, isAdmin: true);

        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == node.Id)
                                              .ExecuteEntityAsync();

        Assert.That(raw.Embedding, Is.Null,
            "Embedding must remain null on SQLite because the provider is disabled — the embedding step is skipped entirely");
    }

    [Test]
    public async Task UploadContent_TextType_SqliteFixture_ContentIsStored()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await svc.CreateNode(new NodeDetails { Type = "task", Name = "ContentStillStored" }, callerId: 0);
        byte[] content = Encoding.UTF8.GetBytes("stored content");

        await svc.UploadContent(node.Id, "text/plain", new MemoryStream(content), callerId: 0, isAdmin: true);

        (string ct, Stream stream) = await svc.GetNodeData(node.Id, callerId: 0, isAdmin: true);
        byte[] stored = new byte[content.Length];
        await stream.ReadExactlyAsync(stored);

        Assert.Multiple(() => {
            Assert.That(ct, Is.EqualTo("text/plain"));
            Assert.That(stored, Is.EqualTo(content));
        });
    }

    static readonly (string contentType, bool expected)[] TextPredicateCases =
    [
        ("text/plain",                          true),
        ("text/markdown",                       true),
        ("text/html",                           true),
        ("text/csv",                            true),
        ("text/xml",                            true),
        ("text/plain; charset=utf-8",           true),
        ("text/markdown; charset=utf-8",        true),
        ("application/json",                    true),
        ("application/xml",                     true),
        ("application/x-yaml",                  false),
        ("application/yaml",                    false),
        ("application/javascript",              false),
        ("application/x-sh",                    false),
        ("application/octet-stream",            false),
        ("application/pdf",                     false),
        ("image/png",                           false),
        ("image/jpeg",                          false),
        ("audio/mpeg",                          false),
        ("video/mp4",                           false),
        ("",                                    false),
        (null!,                                 false),
        ("TEXT/PLAIN",                          true),
        ("APPLICATION/JSON",                    true),
    ];

    [TestCaseSource(nameof(TextPredicateCases))]
    public void TextContentTypePredicate_IsText_MatchesAllowlist((string contentType, bool expected) testCase)
    {
        bool result = TextContentTypePredicate.IsText(testCase.contentType);
        Assert.That(result, Is.EqualTo(testCase.expected),
            $"IsText(\"{testCase.contentType}\") expected {testCase.expected}");
    }

    [Test]
    public async Task UploadContent_NonTextType_SqliteFixture_EmbeddingRemainsNull()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await svc.CreateNode(new NodeDetails { Type = "asset", Name = "ImageNode" }, callerId: 0);
        byte[] content = [0x89, 0x50, 0x4E, 0x47];

        await svc.UploadContent(node.Id, "image/png", new MemoryStream(content), callerId: 0, isAdmin: true);

        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == node.Id)
                                              .ExecuteEntityAsync();

        Assert.That(raw.Embedding, Is.Null,
            "Embedding must remain null on SQLite for non-text content — entire embedding step skipped");
    }

    [Test]
    public async Task UploadContent_NonTextAfterText_SqliteFixture_EmbeddingRemainsNull()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await svc.CreateNode(new NodeDetails { Type = "asset", Name = "TextThenImage" }, callerId: 0);

        byte[] textContent = Encoding.UTF8.GetBytes("some markdown content");
        await svc.UploadContent(node.Id, "text/markdown", new MemoryStream(textContent), callerId: 0, isAdmin: true);

        byte[] imageContent = [0x89, 0x50, 0x4E, 0x47];
        await svc.UploadContent(node.Id, "image/png", new MemoryStream(imageContent), callerId: 0, isAdmin: true);

        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == node.Id)
                                              .ExecuteEntityAsync();

        Assert.Multiple(() => {
            Assert.That(raw.Embedding, Is.Null,
                "on SQLite the entire embedding block is skipped — Embedding stays null regardless of content type sequence");
            Assert.That(raw.ContentType, Is.EqualTo("image/png"),
                "content type must reflect the most recent upload");
        });
    }

    [Test]
    public void EmbeddingModel_Constant_MatchesExpectedValue()
    {
        Assert.That(TextContentTypePredicate.EmbeddingModel, Is.EqualTo("gemini-embedding-001"));
    }
}
