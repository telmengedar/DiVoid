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
/// the Postgres embedding() call path is not exercised here — the capability flag
/// ensures it is never reached in these tests.
/// </summary>
[TestFixture]
public class EmbeddingTests
{
    static readonly IEmbeddingCapability DisabledCapability = new EmbeddingCapability(false);

    static NodeService MakeService(DatabaseFixture fixture)
        => new(fixture.EntityManager, DisabledCapability);

    // -----------------------------------------------------------------------
    // 1. Capability flag — DI resolution in the SQLite test factory
    // -----------------------------------------------------------------------

    [Test]
    public void EmbeddingCapability_SqliteTestFactory_IsDisabled()
    {
        // WebApplicationFactory uses "Database:Type" = "Sqlite" (see TestSetup.CreateTestFactory).
        // Startup registers EmbeddingCapability(isEnabled: false) for any non-Postgres config.
        // Verify the resolved singleton reports IsEnabled = false.
        using WebApplicationFactory<Program> factory = TestSetup.CreateTestFactory("capability_check");
        IEmbeddingCapability capability = factory.Services.GetRequiredService<IEmbeddingCapability>();
        Assert.That(capability.IsEnabled, Is.False,
            "IEmbeddingCapability.IsEnabled must be false when Database:Type is Sqlite");
    }

    // -----------------------------------------------------------------------
    // 2. Text content upload on SQLite — Embedding stays null
    // -----------------------------------------------------------------------

    [Test]
    public async Task UploadContent_TextType_SqliteFixture_EmbeddingRemainsNull()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await svc.CreateNode(new NodeDetails { Type = "task", Name = "EmbedSkipTest" });
        byte[] content = Encoding.UTF8.GetBytes("# hello\nsome markdown content");

        await svc.UploadContent(node.Id, "text/markdown", new MemoryStream(content));

        // Read Embedding directly from the entity, bypassing the DTO mapper
        // (NodeDetails does not expose Embedding — it is an internal storage field).
        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == node.Id)
                                              .ExecuteEntityAsync();

        Assert.That(raw.Embedding, Is.Null,
            "Embedding must remain null on SQLite because the capability flag is false — the embedding step is skipped entirely");
    }

    [Test]
    public async Task UploadContent_TextType_SqliteFixture_ContentIsStored()
    {
        // Verifies that skipping the embedding step does not break the content write.
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await svc.CreateNode(new NodeDetails { Type = "task", Name = "ContentStillStored" });
        byte[] content = Encoding.UTF8.GetBytes("stored content");

        await svc.UploadContent(node.Id, "text/plain", new MemoryStream(content));

        (string ct, Stream stream) = await svc.GetNodeData(node.Id);
        byte[] stored = new byte[content.Length];
        await stream.ReadExactlyAsync(stored);

        Assert.Multiple(() => {
            Assert.That(ct, Is.EqualTo("text/plain"));
            Assert.That(stored, Is.EqualTo(content));
        });
    }

    // -----------------------------------------------------------------------
    // 3. TextContentTypePredicate — pure unit tests (table-driven)
    // -----------------------------------------------------------------------

    static readonly (string contentType, bool expected)[] TextPredicateCases =
    [
        // text/* allowlist
        ("text/plain",                          true),
        ("text/markdown",                       true),
        ("text/html",                           true),
        ("text/csv",                            true),
        ("text/xml",                            true),
        // text/* with charset suffix
        ("text/plain; charset=utf-8",           true),
        ("text/markdown; charset=utf-8",        true),
        // application/* explicit allowlist
        ("application/json",                    true),
        ("application/xml",                     true),
        ("application/x-yaml",                  true),
        ("application/yaml",                    true),
        ("application/javascript",              true),
        ("application/x-sh",                    true),
        // non-text — must return false
        ("application/octet-stream",            false),
        ("application/pdf",                     false),
        ("image/png",                           false),
        ("image/jpeg",                          false),
        ("audio/mpeg",                          false),
        ("video/mp4",                           false),
        ("",                                    false),
        (null!,                                 false),
        // case insensitivity
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

    // -----------------------------------------------------------------------
    // 4. Clear-on-non-text — non-text upload on SQLite leaves Embedding unchanged
    //    (on SQLite the clear UPDATE is also skipped because capability.IsEnabled = false)
    // -----------------------------------------------------------------------

    [Test]
    public async Task UploadContent_NonTextType_SqliteFixture_EmbeddingRemainsNull()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await svc.CreateNode(new NodeDetails { Type = "asset", Name = "ImageNode" });
        byte[] content = [0x89, 0x50, 0x4E, 0x47]; // PNG magic bytes

        await svc.UploadContent(node.Id, "image/png", new MemoryStream(content));

        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == node.Id)
                                              .ExecuteEntityAsync();

        Assert.That(raw.Embedding, Is.Null,
            "Embedding must remain null on SQLite for non-text content — entire embedding step skipped");
    }

    [Test]
    public async Task UploadContent_NonTextAfterText_SqliteFixture_EmbeddingRemainsNull()
    {
        // Verify the clear-on-non-text path is also skipped on SQLite (capability disabled).
        // Upload text content first (Embedding remains null — capability disabled).
        // Then upload non-text content — the clear UPDATE is also skipped.
        // Result: Embedding is still null, content type has changed.
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await svc.CreateNode(new NodeDetails { Type = "asset", Name = "TextThenImage" });

        // first upload: text content (embedding step skipped on SQLite)
        byte[] textContent = Encoding.UTF8.GetBytes("some markdown content");
        await svc.UploadContent(node.Id, "text/markdown", new MemoryStream(textContent));

        // second upload: non-text (clear step also skipped on SQLite)
        byte[] imageContent = [0x89, 0x50, 0x4E, 0x47]; // PNG magic bytes
        await svc.UploadContent(node.Id, "image/png", new MemoryStream(imageContent));

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

    // -----------------------------------------------------------------------
    // 5. EmbeddingModel constant — sanity check
    // -----------------------------------------------------------------------

    [Test]
    public void EmbeddingModel_Constant_MatchesExpectedValue()
    {
        // Centralised in TextContentTypePredicate per §13 Decision 1 of the architecture doc.
        Assert.That(TextContentTypePredicate.EmbeddingModel, Is.EqualTo("gemini-embedding-001"));
    }
}
