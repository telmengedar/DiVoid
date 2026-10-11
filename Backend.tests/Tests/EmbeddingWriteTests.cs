using System.Text;
using System.Text.RegularExpressions;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Moq;
using NUnit.Framework;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Entities.Operations.Prepared;
using Pooshit.Ocelot.Info;

namespace Backend.tests.Tests;

/// <summary>
/// pins the single UPDATE which every embedding write path builds
/// </summary>
[TestFixture, Parallelizable]
public class EmbeddingWriteTests
{
    static PreparedOperation Prepare(long nodeId, string? name, byte[]? content, string? contentType)
    {
        Mock<IDBClient> client = new();
        client.SetupGet(c => c.DBInfo).Returns(new PostgreInfo());

        return EmbeddingWrite.Build(new EntityManager(client.Object), nodeId, name!, content!, contentType!).Prepare();
    }

    static string Render(PreparedOperation prepared)
    {
        string sql = Regex.Replace(prepared.CommandText, @"\s+", " ");
        return Regex.Replace(sql, @"@(\d+)", m => Convert.ToString(prepared.ConstantParameters[int.Parse(m.Groups[1].Value) - 1], System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: an embeddable input is written as the embedding of its composed text to the row with the given id only")]
    public void Build_EmbeddableInput_SetsEmbeddingOfComposedTextForNodeId()
    {
        string sql = Render(Prepare(42, "name", Encoding.UTF8.GetBytes("body"), "text/markdown"));

        Assert.That(sql, Does.Match(@"^UPDATE node SET ""embedding"" = subvector \( \( embedding \( gemini-embedding-001 , name\n\nbody \) ::vector \) , 1 , 768 \) WHERE .*""id"" = 42$"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: an input without embeddable surface clears the embedding of the row with the given id only")]
    public void Build_NoSurface_SetsEmbeddingNullForNodeId()
    {
        string sql = Render(Prepare(42, "  ", null, null));

        Assert.That(sql, Does.Match(@"^UPDATE node SET ""embedding"" = NULL WHERE .*""id"" = 42$"));
    }
}
