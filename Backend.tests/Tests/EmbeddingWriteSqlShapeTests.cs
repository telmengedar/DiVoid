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
    [Description("DiVoid #16141: the three embedding branches of a name patch are cut to the column dimension")]
    public async Task PatchName_WritesAllThreeEmbeddingBranchesCutToDimension()
    {
        Mock<IDBClient> client = CreateClient(() => NameTable("x"));
        NodeService service = new(new EntityManager(client.Object), new EmbeddingCapability(true));

        await service.Patch(5, [new PatchOperation { Op = "replace", Path = "/name", Value = "renamed" }], callerId: 0, isAdmin: true, CancellationToken.None);

        AssertCutToDimension(EmbeddingValues(Statements(client)), 3);
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
