using System.Reflection;
using System.Text.RegularExpressions;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Moq;
using NUnit.Framework;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Entities.Attributes;
using Pooshit.Ocelot.Info;
using Pooshit.Ocelot.Tokens;

namespace Backend.tests.Tests;

/// <summary>
/// pins the expression which turns text into the vector stored in and compared against <c>node.embedding</c>
/// </summary>
[TestFixture, Parallelizable]
public class EmbeddingExpressionTests
{
    const string Text = "some text";

    static string RenderResolved()
    {
        Mock<IDBClient> client = new();
        client.SetupGet(c => c.DBInfo).Returns(new PostgreInfo());
        IEntityManager em = new EntityManager(client.Object);

        Pooshit.Ocelot.Entities.Operations.Prepared.PreparedOperation prepared =
            em.Update<Node>()
              .Set(n => n.Embedding == EmbeddingExpression.OfText(DB.Constant(Text)).Type<float[]>())
              .Where(n => n.Id == 1)
              .Prepare();

        string sql = Regex.Replace(prepared.CommandText, @"\s+", " ");
        return Regex.Replace(sql, @"@(\d+)", m => Convert.ToString(prepared.ConstantParameters[int.Parse(m.Groups[1].Value) - 1], System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
    }

    [Test, Parallelizable]
    [Description("DiVoid #16141: the embedding of a text is cut to its leading components with subvector(..., 1, D) so writes and query vectors have the same shape")]
    public void OfText_RendersSubvectorFromFirstComponentToDimension()
    {
        string sql = RenderResolved();

        Assert.That(sql, Does.Match(@"subvector\s*\(\s*\(?\s*embedding\s*\(\s*gemini-embedding-001\s*,\s*some text\s*\)\s*::vector\s*\)?\s*,\s*1\s*,\s*768\s*\)"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16141: the dimension the expression cuts to equals the dimension the node.embedding column is declared with")]
    public void OfText_DimensionEqualsNodeEmbeddingColumnDimension()
    {
        VectorAttribute column = typeof(Node).GetProperty(nameof(Node.Embedding))!.GetCustomAttribute<VectorAttribute>()!;

        System.Text.RegularExpressions.Match rendered = Regex.Match(RenderResolved(), @",\s*1\s*,\s*(?<dimension>\d+)\s*\)");

        Assert.That(int.Parse(rendered.Groups["dimension"].Value), Is.EqualTo(column.Dimensions));
    }
}
