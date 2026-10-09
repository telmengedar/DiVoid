using Backend.Init;
using Moq;
using NUnit.Framework;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Info;

namespace Backend.tests.Tests;

/// <summary>
/// pins which statements the embedding column migration sends for each live column type
/// </summary>
[TestFixture, Parallelizable]
public class EmbeddingColumnMigrationTests
{
    static (IEntityManager Database, List<string> Statements) CreatePostgres(string? liveType)
    {
        List<string> statements = [];
        Mock<IDBClient> client = new();
        client.SetupGet(c => c.DBInfo).Returns(new PostgreInfo());
        client.Setup(c => c.ScalarAsync(It.IsAny<Transaction>(), It.IsAny<string>(), It.IsAny<object[]>()))
              .Callback<Transaction, string, object[]>((_, sql, _) => statements.Add(sql))
              .ReturnsAsync(liveType);
        client.Setup(c => c.NonQueryAsync(It.IsAny<Transaction>(), It.IsAny<string>(), It.IsAny<object[]>()))
              .Callback<Transaction, string, object[]>((_, sql, _) => statements.Add(sql))
              .ReturnsAsync(0);
        return (new EntityManager(client.Object), statements);
    }

    static IEnumerable<string> Writes(List<string> statements) => statements.Where(s => !s.TrimStart().StartsWith("SELECT"));

    [Test, Parallelizable]
    [Description("DiVoid #16141: a legacy real[] column is first copied to the backup table and then converted keeping the first 768 components")]
    public async Task RealArrayColumn_CopiesToBackupThenTruncatesToVector768()
    {
        (IEntityManager database, List<string> statements) = CreatePostgres("real[]");

        await EmbeddingColumnMigration.RunAsync(database, null!);

        string[] writes = Writes(statements).ToArray();
        Assert.Multiple(() => {
            Assert.That(writes, Has.Length.EqualTo(2));
            Assert.That(writes[0], Does.StartWith("CREATE TABLE node_embedding_backup AS SELECT id, embedding FROM node"));
            Assert.That(writes[1], Is.EqualTo("ALTER TABLE node ALTER COLUMN embedding TYPE vector(768) USING subvector(embedding::vector, 1, 768)"));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16141: a column which already is vector(768) is left alone, so a second startup neither re-truncates nor touches the backup")]
    public async Task Vector768Column_SendsNoWrite()
    {
        (IEntityManager database, List<string> statements) = CreatePostgres("vector(768)");

        await EmbeddingColumnMigration.RunAsync(database, null!);

        Assert.That(Writes(statements), Is.Empty);
    }

    [Test, Parallelizable]
    [Description("DiVoid #16141: a database without node.embedding yet is left to schema creation")]
    public async Task MissingColumn_SendsNoWrite()
    {
        (IEntityManager database, List<string> statements) = CreatePostgres(null);

        await EmbeddingColumnMigration.RunAsync(database, null!);

        Assert.That(Writes(statements), Is.Empty);
    }

    [Test, Parallelizable]
    [Description("DiVoid #16141: a column of any other type, including a vector of another dimension, stops startup with both types named and writes nothing")]
    [TestCase("vector(3072)")]
    [TestCase("vector(1536)")]
    [TestCase("text")]
    public void UnexpectedColumnType_ThrowsNamingLiveAndExpectedType(string liveType)
    {
        (IEntityManager database, List<string> statements) = CreatePostgres(liveType);

        InvalidOperationException? thrown = Assert.ThrowsAsync<InvalidOperationException>(() => EmbeddingColumnMigration.RunAsync(database, null!));

        Assert.Multiple(() => {
            Assert.That(thrown!.Message, Does.Contain(liveType).And.Contain("vector(768)"));
            Assert.That(Writes(statements), Is.Empty);
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16141: databases without vector support are not inspected at all")]
    public async Task Sqlite_SendsNothing()
    {
        Mock<IDBClient> client = new();
        client.SetupGet(c => c.DBInfo).Returns(new SQLiteInfo());
        IEntityManager database = new EntityManager(client.Object);

        await EmbeddingColumnMigration.RunAsync(database, null!);

        Assert.That(client.Invocations.Where(i => i.Method.Name != "get_DBInfo"), Is.Empty);
    }
}
