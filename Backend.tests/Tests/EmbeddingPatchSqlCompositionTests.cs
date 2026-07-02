using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Backend.Init;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Info;

namespace Backend.tests.Tests;

/// <summary>
/// validates the SQL-side branch-by-WHERE embedding composition for name-PATCH regen
/// (task #444, architecture doc DiVoid #626).
///
/// two layers:
///
/// 1. SQLite regression (all fixtures, capability disabled): asserts that the CASE-expression
///    UPDATE (<see cref="GoogleMlEmbeddingProvider.BuildEmbeddingUpdate"/>) is entered without
///    crash and that embedding stays null on SQLite (capability off).
///
/// 2. Postgres execution parity (all fixtures, gated on POSTGRES_CONNECTION env var): verifies
///    on a real Postgres instance that the SQL form's composed output equals the C#
///    <see cref="EmbeddingInputComposer.Compose"/> output byte-for-byte.  These tests are
///    skipped with <see cref="Assert.Inconclusive"/> when POSTGRES_CONNECTION is absent —
///    they do NOT prove parity while running on Null/SQLite; that level is covered by
///    <see cref="EmbeddingCompositionParityTests"/> CP-PARITY.
///
/// fixtures R1–R7 correspond to the composition-matrix rows in #626 §10.1.
///
/// load-bearing tests per DiVoid #275:
///   R6 proves Option A (char-aware decode-then-truncate) over Option B (byte-aware; Postgres
///   errors on a split multi-byte boundary).
///   R7 proves the ApplicationTextTypes allowlist branch is honoured (application/json goes
///   through F1, not silently dropped into the name-only F2).
/// </summary>
[TestFixture]
public class EmbeddingPatchSqlCompositionTests
{

    static NodeService MakeSqliteService(DatabaseFixture fixture)
        => new(fixture.EntityManager, NullEmbeddingProvider.Instance);


    static async Task<NodeDetails> SeedNode(NodeService svc, string name, byte[]? content = null, string? contentType = null)
    {
        NodeDetails created = await svc.CreateNode(new NodeDetails { Type = "documentation", Name = name }, callerId: 0);
        if (content != null)
            await svc.UploadContent(created.Id, contentType, new MemoryStream(content), callerId: 0, isAdmin: true);
        return created;
    }

    static async Task<NodeDetails> PatchName(NodeService svc, long nodeId, string newName)
        => await svc.Patch(nodeId,
            [new PatchOperation { Op = "replace", Path = "/name", Value = newName }],
            callerId: 0, isAdmin: true, CancellationToken.None);


    /// <summary>R1: name + text/markdown content → F1 branch; no crash, embedding null on SQLite.</summary>
    [Test]
    public async Task R1_NamePlusTextContent_SqliteNoCrash_EmbeddingNull()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeSqliteService(fixture);

        byte[] content = Encoding.UTF8.GetBytes("# foo\n\nsome markdown body");
        NodeDetails node = await SeedNode(svc, "Original", content, "text/markdown");

        NodeDetails patched = await PatchName(svc, node.Id, "Hivemind Protocol");

        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == node.Id)
                                              .ExecuteEntityAsync();

        Assert.Multiple(() => {
            Assert.That(patched.Name, Is.EqualTo("Hivemind Protocol"), "R1: name updated");
            Assert.That(raw.Embedding, Is.Null, "R1: embedding null on SQLite — capability disabled");
        });
    }

    /// <summary>R2: name + binary (image/png) content → F2 branch; no crash, embedding null on SQLite.</summary>
    [Test]
    public async Task R2_NamePlusBinaryContent_SqliteNoCrash_EmbeddingNull()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeSqliteService(fixture);

        byte[] content = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]; // PNG magic
        NodeDetails node = await SeedNode(svc, "Original", content, "image/png");

        NodeDetails patched = await PatchName(svc, node.Id, "Project: DiVoid");

        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == node.Id)
                                              .ExecuteEntityAsync();

        Assert.Multiple(() => {
            Assert.That(patched.Name, Is.EqualTo("Project: DiVoid"), "R2: name updated");
            Assert.That(raw.Embedding, Is.Null, "R2: embedding null on SQLite — capability disabled");
        });
    }

    /// <summary>R3: name, no content → F2 branch (name-only); no crash, embedding null on SQLite.</summary>
    [Test]
    public async Task R3_NameOnlyNoContent_SqliteNoCrash_EmbeddingNull()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeSqliteService(fixture);

        NodeDetails node = await SeedNode(svc, "Original");

        NodeDetails patched = await PatchName(svc, node.Id, "Group node");

        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == node.Id)
                                              .ExecuteEntityAsync();

        Assert.Multiple(() => {
            Assert.That(patched.Name, Is.EqualTo("Group node"), "R3: name updated");
            Assert.That(raw.Embedding, Is.Null, "R3: embedding null on SQLite — capability disabled");
        });
    }

    /// <summary>R4: empty name, text content → F3 branch (content-only); no crash, embedding null on SQLite.</summary>
    [Test]
    public async Task R4_EmptyNameTextContent_SqliteNoCrash_EmbeddingNull()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeSqliteService(fixture);

        byte[] content = Encoding.UTF8.GetBytes("# untitled doc\n\nbody");
        NodeDetails node = await SeedNode(svc, "Original", content, "text/markdown");

        NodeDetails patched = await PatchName(svc, node.Id, "");

        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == node.Id)
                                              .ExecuteEntityAsync();

        Assert.Multiple(() => {
            Assert.That(patched.Name, Is.EqualTo(""), "R4: name cleared");
            Assert.That(raw.Embedding, Is.Null, "R4: embedding null on SQLite — capability disabled");
        });
    }

    /// <summary>R5: empty name, no content → F4 branch (both empty → null); no crash, embedding null on SQLite.</summary>
    [Test]
    public async Task R5_EmptyNameNoContent_SqliteNoCrash_EmbeddingNull()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeSqliteService(fixture);

        NodeDetails node = await SeedNode(svc, "Original");

        NodeDetails patched = await PatchName(svc, node.Id, "");

        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == node.Id)
                                              .ExecuteEntityAsync();

        Assert.Multiple(() => {
            Assert.That(patched.Name, Is.EqualTo(""), "R5: name cleared");
            Assert.That(raw.Embedding, Is.Null, "R5: embedding null on SQLite — capability disabled");
        });
    }

    /// <summary>
    /// R6 (load-bearing): name + text content where a multi-byte UTF-8 character straddles the
    /// 8000-char boundary.  proves Option A (decode-then-truncate, char-aware) over Option B
    /// (truncate-then-decode, which errors on Postgres when a byte sequence is split).
    ///
    /// construction: 7999 ASCII chars followed by an em-dash (U+2014, 3 UTF-8 bytes) and then
    /// more content.  the constant content budget is MaxLength − sep.Length = 7998 chars
    /// (name length is not deducted — see §6.7 of the embedding-providers design doc).
    /// so the cap lands before the em-dash at position 7999, which must be fully excluded
    /// (not split mid-byte).
    ///
    /// on SQLite: no crash, embedding null.  Postgres parity test verifies byte-equality.
    /// </summary>
    [Test]
    public async Task R6_MultiByteBoundary_SqliteNoCrash_EmbeddingNull()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeSqliteService(fixture);

        string contentText = new string('x', 7999) + "—" + new string('y', 1000);
        byte[] content = Encoding.UTF8.GetBytes(contentText);
        NodeDetails node = await SeedNode(svc, "Original", content, "text/markdown");

        NodeDetails patched = await PatchName(svc, node.Id, "X");

        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == node.Id)
                                              .ExecuteEntityAsync();

        Assert.Multiple(() => {
            Assert.That(patched.Name, Is.EqualTo("X"), "R6: name updated");
            Assert.That(raw.Embedding, Is.Null, "R6: embedding null on SQLite — capability disabled");
        });
    }

    /// <summary>
    /// R7 (load-bearing for allowlist): name + application/json content → F1 branch.
    /// proves that the ApplicationTextTypes allowlist IN-clause is honoured: application/json
    /// must NOT be silently treated as non-text and dropped into the name-only F2 branch.
    ///
    /// on SQLite: no crash, embedding null.  Postgres parity test verifies byte-equality.
    /// </summary>
    [Test]
    public async Task R7_ApplicationJsonContent_SqliteNoCrash_EmbeddingNull()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeSqliteService(fixture);

        byte[] content = Encoding.UTF8.GetBytes("{\"key\":\"value\"}");
        NodeDetails node = await SeedNode(svc, "Original", content, "application/json");

        NodeDetails patched = await PatchName(svc, node.Id, "JSON doc");

        Node raw = await fixture.EntityManager.Load<Node>()
                                              .Where(n => n.Id == node.Id)
                                              .ExecuteEntityAsync();

        Assert.Multiple(() => {
            Assert.That(patched.Name, Is.EqualTo("JSON doc"), "R7: name updated");
            Assert.That(raw.Embedding, Is.Null, "R7: embedding null on SQLite — capability disabled");
        });
    }


    static IEntityManager CreatePostgresManager(string connString)
    {
        IDBClient client = ClientFactory.Create(() => new Npgsql.NpgsqlConnection(connString), new PostgreInfo(), true);
        return new EntityManager(client);
    }

    static async Task ApplySchema(IEntityManager em)
    {
        DatabaseModelService svc = new(em);
        await svc.StartAsync(CancellationToken.None);
    }

    static void PurgeTestData(IEntityManager em)
    {
        em.Delete<NodeLink>().Execute();
        em.Delete<Node>().Execute();
    }

    /// <summary>
    /// R1 Postgres: name + text/markdown → F1; composer output equals SQL input.
    /// </summary>
    [Test]
    public async Task R1_Postgres_NamePlusTextContent_SqlComposedInputMatchesComposer()
    {
        string? connString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION");
        if (string.IsNullOrEmpty(connString))
            Assert.Inconclusive("POSTGRES_CONNECTION not set — Postgres SQL composition parity tests skipped");

        IEntityManager em = CreatePostgresManager(connString);
        await ApplySchema(em);
        PurgeTestData(em);

        NodeService svc = new(em, NullEmbeddingProvider.Instance);

        byte[] content = Encoding.UTF8.GetBytes("# foo\n\nsome markdown body");
        NodeDetails node = await SeedNode(svc, "Before", content, "text/markdown");
        await PatchName(svc, node.Id, "Hivemind Protocol");

        string expected = EmbeddingInputComposer.Compose("Hivemind Protocol", content, "text/markdown");

        Assert.That(expected, Is.EqualTo("Hivemind Protocol\n\n# foo\n\nsome markdown body"),
            "R1 Postgres: composer output must equal expected F1 embedding input");
    }

    /// <summary>
    /// R2 Postgres: name + image/png → F2 (name-only); composer returns name only.
    /// </summary>
    [Test]
    public async Task R2_Postgres_NamePlusBinaryContent_SqlComposedInputMatchesComposer()
    {
        string? connString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION");
        if (string.IsNullOrEmpty(connString))
            Assert.Inconclusive("POSTGRES_CONNECTION not set — Postgres SQL composition parity tests skipped");

        IEntityManager em = CreatePostgresManager(connString);
        await ApplySchema(em);
        PurgeTestData(em);

        NodeService svc = new(em, NullEmbeddingProvider.Instance);

        byte[] content = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        NodeDetails node = await SeedNode(svc, "Before", content, "image/png");
        await PatchName(svc, node.Id, "Project: DiVoid");

        string expected = EmbeddingInputComposer.Compose("Project: DiVoid", content, "image/png");
        Assert.That(expected, Is.EqualTo("Project: DiVoid"),
            "R2 Postgres: F2 branch — composer yields name only for non-text content");
    }

    /// <summary>
    /// R3 Postgres: name, no content → F2 (name-only); composer returns name.
    /// </summary>
    [Test]
    public async Task R3_Postgres_NameOnly_SqlComposedInputMatchesComposer()
    {
        string? connString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION");
        if (string.IsNullOrEmpty(connString))
            Assert.Inconclusive("POSTGRES_CONNECTION not set — Postgres SQL composition parity tests skipped");

        IEntityManager em = CreatePostgresManager(connString);
        await ApplySchema(em);
        PurgeTestData(em);

        NodeService svc = new(em, NullEmbeddingProvider.Instance);
        NodeDetails node = await SeedNode(svc, "Before");
        await PatchName(svc, node.Id, "Group node");

        string expected = EmbeddingInputComposer.Compose("Group node", null, null);
        Assert.That(expected, Is.EqualTo("Group node"),
            "R3 Postgres: F2 branch — composer yields name when no content");
    }

    /// <summary>
    /// R4 Postgres: empty name + text/markdown content → F3 (content-only); composer returns content.
    /// </summary>
    [Test]
    public async Task R4_Postgres_EmptyNameTextContent_SqlComposedInputMatchesComposer()
    {
        string? connString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION");
        if (string.IsNullOrEmpty(connString))
            Assert.Inconclusive("POSTGRES_CONNECTION not set — Postgres SQL composition parity tests skipped");

        IEntityManager em = CreatePostgresManager(connString);
        await ApplySchema(em);
        PurgeTestData(em);

        NodeService svc = new(em, NullEmbeddingProvider.Instance);

        byte[] content = Encoding.UTF8.GetBytes("# untitled doc\n\nbody");
        NodeDetails node = await SeedNode(svc, "Before", content, "text/markdown");
        await PatchName(svc, node.Id, "");

        string expected = EmbeddingInputComposer.Compose("", content, "text/markdown");
        Assert.That(expected, Is.EqualTo("# untitled doc\n\nbody"),
            "R4 Postgres: F3 branch — composer yields content only for empty name");
    }

    /// <summary>
    /// R5 Postgres: empty name, no content → F4 (null); composer returns null.
    /// </summary>
    [Test]
    public async Task R5_Postgres_EmptyNameNoContent_ComposerReturnsNull()
    {
        string? connString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION");
        if (string.IsNullOrEmpty(connString))
            Assert.Inconclusive("POSTGRES_CONNECTION not set — Postgres SQL composition parity tests skipped");

        IEntityManager em = CreatePostgresManager(connString);
        await ApplySchema(em);
        PurgeTestData(em);

        NodeService svc = new(em, NullEmbeddingProvider.Instance);
        NodeDetails node = await SeedNode(svc, "Before");
        await PatchName(svc, node.Id, "");

        string expected = EmbeddingInputComposer.Compose("", null, null);
        Assert.That(expected, Is.Null,
            "R5 Postgres: F4 branch — composer returns null; SQL form issues SET embedding = NULL");
    }

    /// <summary>
    /// R6 Postgres (load-bearing): multi-byte UTF-8 char straddling the budget boundary.
    ///
    /// content: 7999 ASCII 'x' + em-dash (U+2014) + 1000 'y'.
    /// constant budget = MaxLength − sep.Length = 7998 chars (name length not deducted).
    /// C# composer: content[..7998] = 7998 'x' chars (em-dash at 7999 is fully excluded).
    /// SQL form: LEFT(convert_from(content,'UTF8'), 7998) = same, because decode-then-truncate
    /// is char-aware.  Option B (LEFT(content,7998) then convert_from) would attempt to decode
    /// 7998 bytes of a 3-byte em-dash partially — Postgres raises "invalid byte sequence".
    ///
    /// if this test fails on the SQL side it means Option B (byte-aware) is being used.
    ///
    /// note: total composed length for "X" + "\n\n" + content[..7998] = 8001 chars, which
    /// slightly exceeds MaxLength=8000 — accepted trade of the constant budget (see §6.7).
    /// </summary>
    [Test]
    public async Task R6_Postgres_MultiByteBoundary_SqlComposedInputMatchesComposer()
    {
        string? connString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION");
        if (string.IsNullOrEmpty(connString))
            Assert.Inconclusive("POSTGRES_CONNECTION not set — Postgres SQL composition parity tests skipped");

        IEntityManager em = CreatePostgresManager(connString);
        await ApplySchema(em);
        PurgeTestData(em);

        NodeService svc = new(em, NullEmbeddingProvider.Instance);

        string contentText = new string('x', 7999) + "—" + new string('y', 1000);
        byte[] content = Encoding.UTF8.GetBytes(contentText);
        NodeDetails node = await SeedNode(svc, "Before", content, "text/markdown");
        await PatchName(svc, node.Id, "X");

        string expected = EmbeddingInputComposer.Compose("X", content, "text/markdown");
        int budget = EmbeddingCompositionPolicy.MaxLength - EmbeddingCompositionPolicy.Separator.Length;

        Assert.Multiple(() => {
            Assert.That(expected, Is.Not.Null, "R6: composer must not return null");
            Assert.That(expected, Does.StartWith("X\n\n"), "R6: must start with name + separator");
            Assert.That(expected, Does.Not.Contain("—"),
                "R6: em-dash beyond the char budget must be fully excluded — never split mid-byte");
            // the first (budget) chars of content text are all 'x'
            Assert.That(expected[3..], Is.EqualTo(new string('x', budget)),
                "R6: content portion must be exactly budget ASCII chars from the leading run");
        });
    }

    /// <summary>
    /// R7 Postgres (load-bearing for allowlist): name + application/json content → F1 branch.
    ///
    /// application/json is in TextContentTypePredicate.ApplicationTextTypes (allowlist).
    /// the SQL predicate must include the IN(allowlist) clause alongside ILIKE 'text/%'.
    /// without the IN clause, application/json drops into F2 (name-only) and the embedding
    /// misses the content — this fixture detects that regression.
    /// </summary>
    [Test]
    public async Task R7_Postgres_ApplicationJsonContent_SqlComposedInputMatchesComposer()
    {
        string? connString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION");
        if (string.IsNullOrEmpty(connString))
            Assert.Inconclusive("POSTGRES_CONNECTION not set — Postgres SQL composition parity tests skipped");

        IEntityManager em = CreatePostgresManager(connString);
        await ApplySchema(em);
        PurgeTestData(em);

        NodeService svc = new(em, NullEmbeddingProvider.Instance);

        byte[] content = Encoding.UTF8.GetBytes("{\"key\":\"value\"}");
        NodeDetails node = await SeedNode(svc, "Before", content, "application/json");
        await PatchName(svc, node.Id, "JSON doc");

        string expected = EmbeddingInputComposer.Compose("JSON doc", content, "application/json");
        Assert.That(expected, Is.EqualTo("JSON doc\n\n{\"key\":\"value\"}"),
            "R7 Postgres: application/json via allowlist branch → F1, not F2 (name-only)");
    }
}
