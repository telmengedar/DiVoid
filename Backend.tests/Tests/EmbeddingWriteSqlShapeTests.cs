using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Info;

namespace Backend.tests.Tests;

/// <summary>
/// drives the public create, patch, content upload and backfill entry points against a mocked Postgres client and pins that
/// every embedding written by them is cut to the column dimension.
/// </summary>
[TestFixture, Parallelizable]
public class EmbeddingWriteSqlShapeTests
{
    static readonly Regex EmbeddingSet = new(@"SET ""embedding"" = (?<value>subvector \( \( embedding \( gemini-embedding-001 , .*? \) ::vector \) , 1 , 768 \)|.*?) (WHERE|$)", RegexOptions.Compiled | RegexOptions.Singleline);

    sealed class MockDefaults : DefaultValueProvider
    {
        protected override object GetDefaultValue(Type type, Mock mock)
        {
            if (type == typeof(Task<int>))
                return Task.FromResult(1);
            if (type == typeof(Task<object>))
                return Task.FromResult<object>(1L);
            return (type.IsValueType ? Activator.CreateInstance(type) : null)!;
        }
    }

    static Reader ReaderOf(DataTable table) => new(new DataTableReader(table), null!, new PostgreInfo());

    static DataTable NodeRow()
    {
        DataTable table = new();
        table.Columns.Add("id", typeof(long));
        table.Columns.Add("type", typeof(string));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("status", typeof(string));
        table.Columns.Add("severity", typeof(int));
        table.Columns.Add("refinement", typeof(string));
        table.Columns.Add("rootnodeid", typeof(long));
        table.Columns.Add("contenttype", typeof(string));
        table.Columns.Add("x", typeof(double));
        table.Columns.Add("y", typeof(double));
        table.Columns.Add("content", typeof(byte[]));
        table.Columns.Add("substance", typeof(string));
        table.Columns.Add("ownerid", typeof(long));
        table.Columns.Add("access", typeof(int));
        table.Columns.Add("created", typeof(DateTime));
        table.Columns.Add("lastupdate", typeof(DateTime));
        table.Rows.Add(5L, "task", "named", DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, 0.0, 0.0, DBNull.Value, DBNull.Value, 0L, 3, DateTime.UtcNow, DateTime.UtcNow);
        return table;
    }

    static IEnumerable<string> Statements(Mock<IDBClient> client)
    {
        return client.Invocations.Select(invocation => {
            string? sql = invocation.Arguments.OfType<string>().FirstOrDefault();
            object[]? parameters = invocation.Arguments.OfType<IEnumerable<object>>().FirstOrDefault()?.ToArray();
            if (sql == null)
                return null;
            string text = Regex.Replace(sql, @"\s+", " ");
            return Regex.Replace(text, @"@(\d+)", m => parameters == null ? m.Value : Convert.ToString(parameters[int.Parse(m.Groups[1].Value) - 1], System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
        }).Where(sql => sql != null)!;
    }

    static IEnumerable<string> EmbeddingValues(IEnumerable<string> statements)
    {
        return statements.Where(sql => sql.StartsWith("UPDATE node SET \"embedding\" = ") && !sql.StartsWith("UPDATE node SET \"embedding\" = NULL") && !sql.StartsWith("UPDATE node SET \"embedding\" = @"))
                         .Select(sql => EmbeddingSet.Match(sql).Groups["value"].Value);
    }

    static Mock<IDBClient> CreateClient(Func<DataTable> candidates)
    {
        Mock<IDBClient> client = new() { DefaultValueProvider = new MockDefaults() };
        client.SetupGet(c => c.DBInfo).Returns(new PostgreInfo());
        client.Setup(c => c.Transaction()).Returns(() => {
            Mock<DbConnection> connection = new();
            connection.Protected().Setup<DbTransaction>("BeginDbTransaction", ItExpr.IsAny<IsolationLevel>()).Returns(new Mock<DbTransaction>().Object);
            return ClientFactory.Create(connection.Object, new PostgreInfo()).Transaction();
        });

        Reader ReaderFor(string sql) => ReaderOf(sql.Contains("INNER JOIN nodetype") ? NodeRow() : candidates());

        client.Setup(c => c.ReaderAsync(It.IsAny<Transaction>(), It.IsAny<string>(), It.IsAny<IEnumerable<object>>(), It.IsAny<CancellationToken>())).ReturnsAsync((Transaction _, string sql, IEnumerable<object> _, CancellationToken _) => ReaderFor(sql));
        client.Setup(c => c.ReaderAsync(It.IsAny<Transaction>(), It.IsAny<string>(), It.IsAny<IEnumerable<object>>())).ReturnsAsync((Transaction _, string sql, IEnumerable<object> _) => ReaderFor(sql));
        client.Setup(c => c.ReaderAsync(It.IsAny<Transaction>(), It.IsAny<string>(), It.IsAny<object[]>())).ReturnsAsync((Transaction _, string sql, object[] _) => ReaderFor(sql));
        client.Setup(c => c.ReaderAsync(It.IsAny<string>(), It.IsAny<IEnumerable<object>>())).ReturnsAsync((string sql, IEnumerable<object> _) => ReaderFor(sql));
        client.Setup(c => c.ReaderAsync(It.IsAny<string>(), It.IsAny<object[]>())).ReturnsAsync((string sql, object[] _) => ReaderFor(sql));
        return client;
    }

    static DataTable NameTable(string name)
    {
        DataTable table = new();
        table.Columns.Add("name", typeof(string));
        table.Rows.Add(name);
        return table;
    }

    static DataTable NodeFields(string? name, string? contentType, byte[]? content)
    {
        DataTable table = new();
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("contenttype", typeof(string));
        table.Columns.Add("content", typeof(byte[]));
        table.Rows.Add(name ?? (object)DBNull.Value, contentType ?? (object)DBNull.Value, content ?? (object)DBNull.Value);
        return table;
    }

    static string[] EmbeddedTexts(Mock<IDBClient> client)
    {
        return client.Invocations.Where(invocation => invocation.Arguments.OfType<string>().FirstOrDefault()?.StartsWith("UPDATE node SET \"embedding\" = subvector") == true)
                     .SelectMany(invocation => invocation.Arguments.OfType<IEnumerable<object>>().First())
                     .OfType<string>()
                     .Where(text => text != TextContentTypePredicate.EmbeddingModel)
                     .ToArray();
    }

    static string[] EmbeddingUpdates(Mock<IDBClient> client)
    {
        return Statements(client).Where(sql => sql.StartsWith("UPDATE node SET \"embedding\" = ")).ToArray();
    }

    static int EmbeddingUpdatesInsideTransaction(Mock<IDBClient> client)
    {
        return client.Invocations.Count(invocation => invocation.Arguments.OfType<string>().FirstOrDefault()?.StartsWith("UPDATE node SET \"embedding\" = ") == true
                                                      && invocation.Arguments.OfType<Transaction>().Any());
    }

    static int ContentLoads(Mock<IDBClient> client)
    {
        return Statements(client).Count(sql => sql.StartsWith("SELECT") && !sql.Contains("INNER JOIN") && sql.Contains("\"content\""));
    }

    static int NullEmbeddingWrites(Mock<IDBClient> client)
    {
        return Statements(client).Count(sql => sql.StartsWith("UPDATE node SET \"embedding\" = NULL"));
    }

    static async Task<string[]> PatchNameEmbeddedTexts(string? storedName, string? contentType, byte[]? content)
    {
        Mock<IDBClient> client = CreateClient(() => NodeFields(storedName, contentType, content));
        NodeService service = new(new EntityManager(client.Object), new EmbeddingCapability(true));

        await service.Patch(5, [new PatchOperation { Op = "replace", Path = "/name", Value = storedName }], callerId: 0, isAdmin: true, CancellationToken.None);

        return EmbeddedTexts(client);
    }

    static void AssertCutToDimension(IEnumerable<string> values, int expectedCount)
    {
        string[] written = values.ToArray();
        Assert.Multiple(() => {
            Assert.That(written, Has.Length.EqualTo(expectedCount));
            Assert.That(written, Has.All.Match(@"(?s)^subvector \( \( embedding \( gemini-embedding-001 , .*? \) ::vector \) , 1 , 768 \)$"));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16141: the embedding written when a node is created is cut to the column dimension")]
    public async Task CreateNode_WritesEmbeddingCutToDimension()
    {
        Mock<IDBClient> client = CreateClient(() => NameTable("x"));
        NodeService service = new(new EntityManager(client.Object), new EmbeddingCapability(true));

        await service.CreateNode(new NodeDetails { Type = "task", Name = "named" }, callerId: 0);

        AssertCutToDimension(EmbeddingValues(Statements(client)), 1);
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: a name patch writes exactly one embedding update, cut to the column dimension")]
    public async Task PatchName_WritesSharedEmbeddingUpdate()
    {
        Mock<IDBClient> client = CreateClient(() => NodeFields("renamed", "text/markdown", Encoding.UTF8.GetBytes("body")));
        NodeService service = new(new EntityManager(client.Object), new EmbeddingCapability(true));

        await service.Patch(5, [new PatchOperation { Op = "replace", Path = "/name", Value = "renamed" }], callerId: 0, isAdmin: true, CancellationToken.None);

        AssertCutToDimension(EmbeddingValues(Statements(client)), 1);
        Assert.That(NullEmbeddingWrites(client), Is.Zero);
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: a name patch embeds the same text a content upload embeds for the same name and content")]
    public async Task PatchName_TextContent_EmbedsSameTextAsContentUpload()
    {
        Mock<IDBClient> uploadClient = CreateClient(() => NameTable("renamed"));
        NodeService uploadService = new(new EntityManager(uploadClient.Object), new EmbeddingCapability(true));
        await uploadService.UploadContent(5, "text/markdown", new MemoryStream(Encoding.UTF8.GetBytes("body")), callerId: 0, isAdmin: true);

        string[] patched = await PatchNameEmbeddedTexts("renamed", "text/markdown", Encoding.UTF8.GetBytes("body"));

        Assert.Multiple(() => {
            Assert.That(patched, Is.EqualTo(new[] { "renamed\n\nbody" }));
            Assert.That(patched, Is.EqualTo(EmbeddedTexts(uploadClient)));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: a name patch on a node with non-text content embeds the same text as creating a node with that name")]
    public async Task PatchName_NonTextContent_EmbedsSameTextAsCreate()
    {
        Mock<IDBClient> createClient = CreateClient(() => NameTable("same"));
        NodeService createService = new(new EntityManager(createClient.Object), new EmbeddingCapability(true));
        await createService.CreateNode(new NodeDetails { Type = "task", Name = "same" }, callerId: 0);

        string[] patched = await PatchNameEmbeddedTexts("same", "image/png", [0x89, 0x50]);

        Assert.Multiple(() => {
            Assert.That(patched, Is.EqualTo(new[] { "same" }));
            Assert.That(patched, Is.EqualTo(EmbeddedTexts(createClient)));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: a name patch keeps the leading and trailing spaces of a non-blank name exactly as create does")]
    public async Task PatchName_PaddedName_EmbedsSameTextAsCreate()
    {
        Mock<IDBClient> createClient = CreateClient(() => NameTable(" foo "));
        NodeService createService = new(new EntityManager(createClient.Object), new EmbeddingCapability(true));
        await createService.CreateNode(new NodeDetails { Type = "task", Name = " foo " }, callerId: 0);

        string[] patched = await PatchNameEmbeddedTexts(" foo ", "image/png", [0x89, 0x50]);

        Assert.Multiple(() => {
            Assert.That(EmbeddedTexts(createClient), Is.EqualTo(new[] { " foo " }));
            Assert.That(patched, Is.EqualTo(EmbeddedTexts(createClient)));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: a whitespace-only name counts as absent on a name patch, so only the content is embedded")]
    public async Task PatchName_WhitespaceOnlyNameWithTextContent_EmbedsContentOnly()
    {
        string[] patched = await PatchNameEmbeddedTexts("   ", "text/markdown", Encoding.UTF8.GetBytes("body"));

        Assert.That(patched, Is.EqualTo(new[] { "body" }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: a whitespace-only name without text content leaves nothing to embed, so the embedding is cleared")]
    public async Task PatchName_WhitespaceOnlyNameWithoutContent_WritesNull()
    {
        Mock<IDBClient> client = CreateClient(() => NodeFields("   ", null, null));
        NodeService service = new(new EntityManager(client.Object), new EmbeddingCapability(true));

        await service.Patch(5, [new PatchOperation { Op = "replace", Path = "/name", Value = "   " }], callerId: 0, isAdmin: true, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(NullEmbeddingWrites(client), Is.EqualTo(1));
            Assert.That(EmbeddedTexts(client), Is.Empty);
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: a name patch cuts the content to the composer budget so the text including the name stays within the maximum length")]
    public async Task PatchName_ContentLongerThanBudget_EmbedsComposerTruncation()
    {
        string content = new('x', EmbeddingInputComposer.MaxLength);

        string[] patched = await PatchNameEmbeddedTexts("ab", "text/markdown", Encoding.UTF8.GetBytes(content));

        Assert.That(patched, Is.EqualTo(new[] { "ab\n\n" + new string('x', EmbeddingInputComposer.MaxLength - 4) }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: a name patch treats a text type with a charset suffix as text, as the composer does")]
    public async Task PatchName_ApplicationTypeWithCharsetSuffix_EmbedsContent()
    {
        string[] patched = await PatchNameEmbeddedTexts("renamed", "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{}"));

        Assert.That(patched, Is.EqualTo(new[] { "renamed\n\n{}" }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: a name patch on a node with empty text content embeds the name only")]
    public async Task PatchName_EmptyTextContent_EmbedsNameOnly()
    {
        string[] patched = await PatchNameEmbeddedTexts("renamed", "text/plain", []);

        Assert.That(patched, Is.EqualTo(new[] { "renamed" }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: a name patch never loads the content blob of a node whose content is not text")]
    public async Task PatchName_NonTextContent_DoesNotLoadContent()
    {
        Mock<IDBClient> client = CreateClient(() => NodeFields("renamed", "image/png", [0x89, 0x50]));
        NodeService service = new(new EntityManager(client.Object), new EmbeddingCapability(true));

        await service.Patch(5, [new PatchOperation { Op = "replace", Path = "/name", Value = "renamed" }], callerId: 0, isAdmin: true, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(EmbeddedTexts(client), Is.Not.Empty);
            Assert.That(ContentLoads(client), Is.Zero);
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: a name patch loads the content of a node whose content is text so it can be embedded")]
    public async Task PatchName_TextContent_LoadsContent()
    {
        Mock<IDBClient> client = CreateClient(() => NodeFields("renamed", "text/plain", Encoding.UTF8.GetBytes("body")));
        NodeService service = new(new EntityManager(client.Object), new EmbeddingCapability(true));

        await service.Patch(5, [new PatchOperation { Op = "replace", Path = "/name", Value = "renamed" }], callerId: 0, isAdmin: true, CancellationToken.None);

        Assert.That(ContentLoads(client), Is.EqualTo(1));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: creating a node with a blank name writes no embedding")]
    public async Task CreateNode_BlankName_WritesNoEmbedding()
    {
        Mock<IDBClient> client = CreateClient(() => NameTable("x"));
        NodeService service = new(new EntityManager(client.Object), new EmbeddingCapability(true));

        await service.CreateNode(new NodeDetails { Type = "task", Name = "   " }, callerId: 0);

        Assert.Multiple(() => {
            Assert.That(EmbeddedTexts(client), Is.Empty);
            Assert.That(NullEmbeddingWrites(client), Is.Zero);
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: the backfill clears the embedding of a candidate which composes to nothing instead of leaving a stale vector")]
    public async Task Backfill_NoEmbeddableSurface_WritesNull()
    {
        DataTable candidates = new();
        candidates.Columns.Add("id", typeof(long));
        candidates.Columns.Add("name", typeof(string));
        candidates.Columns.Add("contenttype", typeof(string));
        candidates.Columns.Add("content", typeof(byte[]));
        candidates.Rows.Add(5L, "   ", DBNull.Value, DBNull.Value);
        Mock<IDBClient> client = CreateClient(() => candidates);
        EmbeddingBackfillService backfill = new(new EntityManager(client.Object), new EmbeddingCapability(true), NullLogger<EmbeddingBackfillService>.Instance);

        await backfill.RunAsync();

        Assert.Multiple(() => {
            Assert.That(NullEmbeddingWrites(client), Is.EqualTo(1));
            Assert.That(EmbeddedTexts(client), Is.Empty);
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16141: the embedding written after a content upload is cut to the column dimension")]
    public async Task UploadContent_WritesEmbeddingCutToDimension()
    {
        Mock<IDBClient> client = CreateClient(() => NameTable("named"));
        NodeService service = new(new EntityManager(client.Object), new EmbeddingCapability(true));

        await service.UploadContent(5, "text/markdown", new MemoryStream(Encoding.UTF8.GetBytes("body")), callerId: 0, isAdmin: true);

        AssertCutToDimension(EmbeddingValues(Statements(client)), 1);
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: the embedding written on node creation targets the created node inside the creating transaction")]
    public async Task CreateNode_EmbeddingWriteTargetsCreatedNodeInsideTransaction()
    {
        Mock<IDBClient> client = CreateClient(() => NameTable("x"));
        NodeService service = new(new EntityManager(client.Object), new EmbeddingCapability(true));

        await service.CreateNode(new NodeDetails { Type = "task", Name = "named" }, callerId: 0);

        Assert.Multiple(() => {
            Assert.That(EmbeddingUpdates(client), Has.Length.EqualTo(1).And.All.EndsWith("\"id\" = 1"));
            Assert.That(EmbeddingUpdatesInsideTransaction(client), Is.EqualTo(1));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: the embedding written by a name patch targets the patched node inside the patch transaction")]
    public async Task PatchName_EmbeddingWriteTargetsPatchedNodeInsideTransaction()
    {
        Mock<IDBClient> client = CreateClient(() => NodeFields("renamed", "text/plain", Encoding.UTF8.GetBytes("body")));
        NodeService service = new(new EntityManager(client.Object), new EmbeddingCapability(true));

        await service.Patch(5, [new PatchOperation { Op = "replace", Path = "/name", Value = "renamed" }], callerId: 0, isAdmin: true, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(EmbeddingUpdates(client), Has.Length.EqualTo(1).And.All.EndsWith("\"id\" = 5"));
            Assert.That(EmbeddingUpdatesInsideTransaction(client), Is.EqualTo(1));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: the embedding written by a content upload targets the uploaded node inside the upload transaction")]
    public async Task UploadContent_EmbeddingWriteTargetsUploadedNodeInsideTransaction()
    {
        Mock<IDBClient> client = CreateClient(() => NameTable("named"));
        NodeService service = new(new EntityManager(client.Object), new EmbeddingCapability(true));

        await service.UploadContent(5, "text/markdown", new MemoryStream(Encoding.UTF8.GetBytes("body")), callerId: 0, isAdmin: true);

        Assert.Multiple(() => {
            Assert.That(EmbeddingUpdates(client), Has.Length.EqualTo(1).And.All.EndsWith("\"id\" = 5"));
            Assert.That(EmbeddingUpdatesInsideTransaction(client), Is.EqualTo(1));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: the backfill writes the embedding of a candidate to the row of that candidate")]
    public async Task Backfill_EmbeddingWriteTargetsCandidateRow()
    {
        DataTable candidates = new();
        candidates.Columns.Add("id", typeof(long));
        candidates.Columns.Add("name", typeof(string));
        candidates.Columns.Add("contenttype", typeof(string));
        candidates.Columns.Add("content", typeof(byte[]));
        candidates.Rows.Add(5L, "named", "text/markdown", Encoding.UTF8.GetBytes("body"));
        Mock<IDBClient> client = CreateClient(() => candidates);
        EmbeddingBackfillService backfill = new(new EntityManager(client.Object), new EmbeddingCapability(true), NullLogger<EmbeddingBackfillService>.Instance);

        await backfill.RunAsync();

        Assert.That(EmbeddingUpdates(client), Has.Length.EqualTo(1).And.All.EndsWith("\"id\" = 5"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: the backfill embeds the composed name and text content of a candidate")]
    public async Task Backfill_NamedTextContent_EmbedsComposedText()
    {
        DataTable candidates = new();
        candidates.Columns.Add("id", typeof(long));
        candidates.Columns.Add("name", typeof(string));
        candidates.Columns.Add("contenttype", typeof(string));
        candidates.Columns.Add("content", typeof(byte[]));
        candidates.Rows.Add(5L, "named", "text/markdown", Encoding.UTF8.GetBytes("body"));
        Mock<IDBClient> client = CreateClient(() => candidates);
        EmbeddingBackfillService backfill = new(new EntityManager(client.Object), new EmbeddingCapability(true), NullLogger<EmbeddingBackfillService>.Instance);

        await backfill.RunAsync();

        Assert.That(EmbeddedTexts(client), Is.EqualTo(new[] { "named\n\nbody" }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16141: the embedding written by the backfill is cut to the column dimension")]
    public async Task Backfill_WritesEmbeddingCutToDimension()
    {
        DataTable candidates = new();
        candidates.Columns.Add("id", typeof(long));
        candidates.Columns.Add("name", typeof(string));
        candidates.Columns.Add("contenttype", typeof(string));
        candidates.Columns.Add("content", typeof(byte[]));
        candidates.Rows.Add(5L, "named", "text/markdown", Encoding.UTF8.GetBytes("body"));
        Mock<IDBClient> client = CreateClient(() => candidates);
        EmbeddingBackfillService backfill = new(new EntityManager(client.Object), new EmbeddingCapability(true), NullLogger<EmbeddingBackfillService>.Instance);

        await backfill.RunAsync();

        AssertCutToDimension(EmbeddingValues(Statements(client)), 1);
    }
}
