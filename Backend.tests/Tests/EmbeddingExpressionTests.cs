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

    static string RenderResolved() => RenderResolved(EmbeddingExpression.OfText(DB.Constant(Text)));

    static string RenderResolved(ISqlToken expression)
    {
        Mock<IDBClient> client = new();
        client.SetupGet(c => c.DBInfo).Returns(new PostgreInfo());
        IEntityManager em = new EntityManager(client.Object);

        Pooshit.Ocelot.Entities.Operations.Prepared.PreparedOperation prepared =
            em.Update<Node>()
              .Set(n => n.Embedding == expression.Type<float[]>())
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

    [Test, Parallelizable]
    [Description("DiVoid #16207: a single text renders exactly the single-text expression, without normalisation")]
    public void OfTexts_SingleText_RendersExactlyOfText()
    {
        string rendered = RenderResolved(EmbeddingExpression.OfTexts([Text]));

        Assert.That(rendered, Is.EqualTo(RenderResolved()));
        Assert.That(rendered, Does.Not.Contain("l2_normalize"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: several texts render the normalised left fold of the normalised single-text vectors with one embedding call per text")]
    public void OfTexts_ThreeTexts_RendersNormalisedVectorAddFoldWithThreeEmbeddings()
    {
        string[] texts = ["first", "second", "third"];
        string[] single = texts.Select(text => Single(RenderResolved(EmbeddingExpression.OfText(DB.Constant(text))))).ToArray();
        string expected = $"l2_normalize ( vector_add ( vector_add ( l2_normalize ( {single[0]} ) , l2_normalize ( {single[1]} ) ) , l2_normalize ( {single[2]} ) ) )";

        string rendered = RenderResolved(EmbeddingExpression.OfTexts(texts));

        Assert.That(rendered, Does.Contain(expected));
        Assert.That(Regex.Matches(rendered, @"embedding \(").Count, Is.EqualTo(3));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: an empty text list is rejected because the caller owns the NULL case")]
    public void OfTexts_NoTexts_Throws()
    {
        Assert.Throws<ArgumentException>(() => EmbeddingExpression.OfTexts([]));
    }

    static string Single(string rendered) => Regex.Match(rendered, @"subvector \(.*\) , 1 , 768 \)").Value;
}
