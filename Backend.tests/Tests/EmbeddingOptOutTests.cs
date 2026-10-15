using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using Moq;
using Moq.Protected;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Info;

namespace Backend.tests.Tests;

/// <summary>
/// pins that a node flagged with <see cref="Node.SkipEmbedding"/> is never embedded by create, content upload, content patch or node patch,
/// and that flipping the flag clears or recomputes the embedding inside the patch request
/// </summary>
[TestFixture, Parallelizable]
public class EmbeddingOptOutTests
{
    const string NullEmbeddingWrite = "UPDATE node SET \"embedding\" = NULL WHERE";

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
        table.Columns.Add("skipembedding", typeof(bool));
        table.Columns.Add("created", typeof(DateTime));
        table.Columns.Add("lastupdate", typeof(DateTime));
        table.Rows.Add(5L, "task", "named", DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, 0.0, 0.0, DBNull.Value, DBNull.Value, 0L, 3, false, DateTime.UtcNow, DateTime.UtcNow);
        return table;
    }

    static DataTable NodeFields(string name, string contentType, byte[] content, bool skipEmbedding)
    {
        DataTable table = new();
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("contenttype", typeof(string));
        table.Columns.Add("content", typeof(byte[]));
        table.Columns.Add("skipembedding", typeof(bool));
        table.Rows.Add(name, contentType, content, skipEmbedding);
        return table;
    }

    static Mock<IDBClient> CreateClient(bool skipEmbedding)
    {
        Mock<IDBClient> client = new() { DefaultValueProvider = new CompletedTaskDefaults() };
        client.SetupGet(c => c.DBInfo).Returns(new PostgreInfo());
        client.Setup(c => c.Transaction()).Returns(() => {
            Mock<DbConnection> connection = new();
            connection.Protected().Setup<DbTransaction>("BeginDbTransaction", ItExpr.IsAny<IsolationLevel>()).Returns(new Mock<DbTransaction>().Object);
            return ClientFactory.Create(connection.Object, new PostgreInfo()).Transaction();
        });

        Reader ReaderFor(string sql) => new(new DataTableReader(sql.Contains("INNER JOIN nodetype") ? NodeRow() : NodeFields("named", "text/markdown", Encoding.UTF8.GetBytes("body"), skipEmbedding)), null!, new PostgreInfo());

        client.Setup(c => c.ReaderAsync(It.IsAny<Transaction>(), It.IsAny<string>(), It.IsAny<IEnumerable<object>>(), It.IsAny<CancellationToken>())).ReturnsAsync((Transaction _, string sql, IEnumerable<object> _, CancellationToken _) => ReaderFor(sql));
        client.Setup(c => c.ReaderAsync(It.IsAny<Transaction>(), It.IsAny<string>(), It.IsAny<IEnumerable<object>>())).ReturnsAsync((Transaction _, string sql, IEnumerable<object> _) => ReaderFor(sql));
        client.Setup(c => c.ReaderAsync(It.IsAny<string>(), It.IsAny<IEnumerable<object>>())).ReturnsAsync((string sql, IEnumerable<object> _) => ReaderFor(sql));
        client.Setup(c => c.ReaderAsync(It.IsAny<string>(), It.IsAny<object[]>())).ReturnsAsync((string sql, object[] _) => ReaderFor(sql));
        return client;
    }

    static string[] Statements(Mock<IDBClient> client)
    {
        return client.Invocations
                     .Select(invocation => invocation.Arguments.OfType<string>().FirstOrDefault())
                     .Where(sql => sql != null)
                     .Select(sql => Regex.Replace(sql!, @"\s+", " "))
                     .ToArray();
    }

    static NodeService CreateService(Mock<IDBClient> client, RecordingEmbeddingProducer producer) => new(new EntityManager(client.Object), new EmbeddingCapability(true), producer);

    static PatchOperation[] Replace(string path, object value) => [new PatchOperation { Op = "replace", Path = path, Value = value }];

    [Test, Parallelizable]
    [Description("DiVoid #22474: a node created with skipEmbedding is inserted with the flag and its name is not embedded")]
    public async Task CreateNode_SkipEmbedding_ProducerNotCalled()
    {
        RecordingEmbeddingProducer producer = new();
        Mock<IDBClient> client = CreateClient(skipEmbedding: true);

        await CreateService(client, producer).CreateNode(new NodeDetails { Type = "task", Name = "named", SkipEmbedding = true }, callerId: 0);

        Assert.Multiple(() => {
            Assert.That(Statements(client), Has.One.StartsWith("INSERT INTO node").And.Contain("skipembedding"));
            Assert.That(producer.Calls, Is.Empty);
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #22474: a content upload on a flagged node writes the content and does not embed")]
    public async Task UploadContent_SkipEmbeddingNode_ProducerNotCalled()
    {
        RecordingEmbeddingProducer producer = new();
        Mock<IDBClient> client = CreateClient(skipEmbedding: true);

        await CreateService(client, producer).UploadContent(5, "text/markdown", new MemoryStream(Encoding.UTF8.GetBytes("body")), callerId: 0, isAdmin: true);

        Assert.Multiple(() => {
            Assert.That(Statements(client), Has.Some.StartsWith("UPDATE node SET \"contenttype\""));
            Assert.That(producer.Calls, Is.Empty);
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #22474: a content patch on a flagged node writes the edited content and does not embed")]
    public async Task PatchContent_SkipEmbeddingNode_ProducerNotCalled()
    {
        RecordingEmbeddingProducer producer = new();
        Mock<IDBClient> client = CreateClient(skipEmbedding: true);

        await CreateService(client, producer).PatchContent(5, [new ContentEdit { Unit = ContentEditUnit.Char, Start = 0, Length = 0, Value = "y" }], callerId: 0, isAdmin: true, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(Statements(client), Has.Some.StartsWith("UPDATE node SET \"content\""));
            Assert.That(producer.Calls, Is.Empty);
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #22474: a name patch on a flagged node writes a NULL embedding and does not embed")]
    public async Task PatchName_SkipEmbeddingNode_WritesNullWithoutProducer()
    {
        RecordingEmbeddingProducer producer = new();
        Mock<IDBClient> client = CreateClient(skipEmbedding: true);

        await CreateService(client, producer).Patch(5, Replace("/name", "renamed"), callerId: 0, isAdmin: true, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(Statements(client), Has.Some.StartsWith(NullEmbeddingWrite));
            Assert.That(producer.Calls, Is.Empty);
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #22474: patching skipEmbedding to true clears the embedding in the same request without embedding")]
    public async Task PatchSkipEmbeddingTrue_WritesNullEmbedding()
    {
        RecordingEmbeddingProducer producer = new();
        Mock<IDBClient> client = CreateClient(skipEmbedding: true);

        await CreateService(client, producer).Patch(5, Replace("/skipEmbedding", true), callerId: 0, isAdmin: true, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(Statements(client), Has.Some.StartsWith("UPDATE node SET \"skipembedding\""));
            Assert.That(Statements(client), Has.Some.StartsWith(NullEmbeddingWrite));
            Assert.That(producer.Calls, Is.Empty);
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #22474: patching skipEmbedding to false re-embeds name plus content in the same request")]
    public async Task PatchSkipEmbeddingFalse_ReEmbedsThroughProducer()
    {
        RecordingEmbeddingProducer producer = new();
        Mock<IDBClient> client = CreateClient(skipEmbedding: false);

        await CreateService(client, producer).Patch(5, Replace("/skipEmbedding", false), callerId: 0, isAdmin: true, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(Statements(client), Has.Some.StartsWith("UPDATE node SET \"skipembedding\""));
            Assert.That(producer.Calls, Is.EqualTo(new[] { ("named\n\nbody", EmbeddingPurpose.Document) }));
        });
    }
}
