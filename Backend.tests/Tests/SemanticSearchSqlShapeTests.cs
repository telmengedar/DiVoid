using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Moq;
using NUnit.Framework;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Info;

namespace Backend.tests.Tests;

/// <summary>
/// pins the SQL shape of semantic search requests as sent to Postgres
/// </summary>
[TestFixture, Parallelizable]
public class SemanticSearchSqlShapeTests
{
    const string QueryText = "semantic search latency";

    static readonly Regex LateralPattern = new(@"INNER JOIN LATERAL \( SELECT .*? OFFSET (?<offset>\S+) \) AS (?<alias>\w+) ON TRUE", RegexOptions.Compiled);

    sealed class StatementCaptured(string sql, object[] parameters) : Exception
    {
        public string Sql { get; } = sql;

        public object[] Parameters { get; } = parameters;
    }

    sealed record Statement(string Sql, object[] Parameters)
    {
        public string Text => Regex.Replace(Sql, @"\s+", " ");

        public string SelectList => Text[..Text.IndexOf(" FROM ", StringComparison.Ordinal)];

        public string Where => Text[Text.IndexOf(" WHERE ", StringComparison.Ordinal)..];

        public string Resolved => Regex.Replace(Text, @"@(\d+)", m => Convert.ToString(Parameters[int.Parse(m.Groups[1].Value) - 1], System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");

        public long OffsetValue(string token)
        {
            if (long.TryParse(token, out long literal))
                return literal;
            int index = int.Parse(Regex.Match(token, @"\d+").Value);
            return Convert.ToInt64(Parameters[index - 1]);
        }
    }

    static NodeService CreateService()
    {
        Mock<IDBClient> client = new();
        client.SetupGet(c => c.DBInfo).Returns(new PostgreInfo());
        client.Setup(c => c.ReaderAsync(It.IsAny<Transaction>(), It.IsAny<string>(), It.IsAny<IEnumerable<object>>(), It.IsAny<CancellationToken>()))
              .Callback<Transaction, string, IEnumerable<object>, CancellationToken>((_, sql, parameters, _) => throw new StatementCaptured(sql, parameters.ToArray()));
        return new(new EntityManager(client.Object), new EmbeddingCapability(true));
    }

    static Statement CaptureList(NodeFilter filter)
    {
        NodeService service = CreateService();
        StatementCaptured captured = Assert.ThrowsAsync<StatementCaptured>(() => service.ListPaged(filter, callerId: 0, isAdmin: true))!;
        return new(captured.Sql, captured.Parameters);
    }

    static Statement CapturePath(NodePathFilter filter)
    {
        NodeService service = CreateService();
        StatementCaptured captured = Assert.ThrowsAsync<StatementCaptured>(() => service.ListPagedByPath(filter, callerId: 0, isAdmin: true, CancellationToken.None))!;
        return new(captured.Sql, captured.Parameters);
    }

    static int Count(string text, string needle) => Regex.Matches(text, Regex.Escape(needle)).Count;

    static NodeFilter FloorFilter() => new() { Query = QueryText, MinSimilarity = 0.5f, Count = 10 };

    [Test, Parallelizable]
    [Description("DiVoid #16141: the embedding() call is planned once per request, not once per floor, order and projection occurrence")]
    public void SingleQueryWithFloor_EmbeddingCallRenderedOnce()
    {
        Statement statement = CaptureList(FloorFilter());

        Assert.Multiple(() => {
            Assert.That(Regex.Matches(statement.Text, @"(?<!"")\bembedding\s*\(").Count, Is.EqualTo(1));
            Assert.That(statement.Parameters.Count(p => Equals(p, QueryText)), Is.EqualTo(1));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16141: the cosine distance is evaluated once per row, not again for the sort and projection")]
    public void SingleQueryWithFloor_CosineOperatorRenderedOnce()
    {
        Statement statement = CaptureList(FloorFilter());

        Assert.That(Count(statement.Text, "<=>"), Is.EqualTo(1));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16141: both sub-selects carry OFFSET 0 so the planner cannot re-inline them into floor, order and projection")]
    public void SemanticLaterals_BothCarryOffsetZero()
    {
        Statement statement = CaptureList(FloorFilter());

        MatchCollection laterals = LateralPattern.Matches(statement.Text);
        Assert.Multiple(() => {
            Assert.That(laterals.Select(m => m.Groups["alias"].Value), Is.EqualTo(new[] { "q", "sim" }));
            Assert.That(laterals.Select(m => statement.OffsetValue(m.Groups["offset"].Value)), Is.All.EqualTo(0L));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16141: projection, floor and ORDER BY read the precomputed similarity column instead of recomputing the distance")]
    public void FloorAndOrderBy_ReferenceSimSimilarityColumn()
    {
        Statement statement = CaptureList(FloorFilter());

        Assert.Multiple(() => {
            Assert.That(statement.SelectList, Does.Contain("sim.\"similarity\""));
            Assert.That(statement.Where, Does.Contain("sim.\"similarity\" >="));
            Assert.That(statement.Where, Does.Contain("ORDER BY sim.\"similarity\" DESC , node.\"id\""));
            Assert.That(statement.Where, Does.Not.Contain("<=>"));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16141: the query vector stays inside its sub-select and is not carried through the rows the sort and window run over")]
    public void QueryVector_IsNotProjectedIntoOuterSelect()
    {
        Statement statement = CaptureList(FloorFilter());

        Assert.Multiple(() => {
            Assert.That(statement.Text, Does.Contain("AS q ON TRUE"));
            Assert.That(statement.SelectList, Does.Not.Contain("q.\"v\""));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16141: the two sub-selects compute the unchanged similarity expression, one minus cosine distance, from the single embedded query")]
    public void SemanticLaterals_RenderUnchangedSimilarityExpressionWithResolvedValues()
    {
        Statement statement = CaptureList(FloorFilter());

        MatchCollection laterals = Regex.Matches(statement.Resolved, @"INNER JOIN LATERAL \( (?<body>SELECT .*?) \) AS (?<alias>\w+) ON TRUE");
        Assert.Multiple(() => {
            Assert.That(laterals.Select(m => m.Groups["body"].Value), Is.EqualTo(new[] {
                "SELECT subvector ( ( embedding ( gemini-embedding-001 , semantic search latency ) ::vector ) , 1 , 768 ) AS v OFFSET 0",
                "SELECT ( 1 - CAST( q.\"v\" <=> ( node.\"embedding\" ::vector ) AS FLOAT) ) AS similarity OFFSET 0"
            }));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16141: without a floor the same single evaluation shape applies and no floor predicate is rendered")]
    public void SingleQueryWithoutFloor_RendersEmbeddingAndCosineOnce()
    {
        Statement statement = CaptureList(new() { Query = QueryText, Count = 10 });

        Assert.Multiple(() => {
            Assert.That(Count(statement.Text, "<=>"), Is.EqualTo(1));
            Assert.That(statement.Parameters.Count(p => Equals(p, QueryText)), Is.EqualTo(1));
            Assert.That(statement.Text, Does.Not.Contain("sim.\"similarity\" >="));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16141: the windowed total count is still injected into the semantic statement")]
    public void SingleQuery_StillCarriesWindowedCount()
    {
        Statement statement = CaptureList(FloorFilter());

        Assert.That(statement.Text, Does.Contain("COUNT(*) OVER()").IgnoreCase);
    }

    [Test, Parallelizable]
    [Description("DiVoid #16141: caller predicates and the embedding null check stay in WHERE next to the similarity floor")]
    public void SingleQuery_KeepsCallerPredicatesAndNullCheck()
    {
        Statement statement = CaptureList(new() { Query = QueryText, MinSimilarity = 0.5f, Type = ["task"], Count = 10 });

        Assert.Multiple(() => {
            Assert.That(statement.Where, Does.Contain("nodetype"));
            Assert.That(statement.Where, Does.Contain("node.\"embedding\" IS NOT NULL"));
            Assert.That(statement.Where, Does.Contain("sim.\"similarity\" >="));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16141: ordering still reads the sim column when the caller did not request the similarity field")]
    public void FieldsWithoutSimilarity_StillOrderBySimColumn()
    {
        Statement statement = CaptureList(new() { Query = QueryText, Fields = ["id", "name"], Count = 10 });

        Assert.Multiple(() => {
            Assert.That(statement.SelectList, Does.Not.Contain("similarity"));
            Assert.That(statement.Where, Does.Contain("ORDER BY sim.\"similarity\" DESC , node.\"id\""));
            Assert.That(Count(statement.Text, "<=>"), Is.EqualTo(1));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16141: a request without a query renders neither sub-select nor a distance")]
    public void ListWithoutQuery_RendersNoLateralJoin()
    {
        Statement statement = CaptureList(new() { Count = 10 });

        Assert.Multiple(() => {
            Assert.That(statement.Text, Does.Not.Contain("LATERAL"));
            Assert.That(statement.Text, Does.Not.Contain("<=>"));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16141: path mode inherits the fenced sub-selects through the mapper operation and keeps its hop predicate")]
    public void PathQuery_SemanticTerminal_HasFencedLateralsAndHopPredicate()
    {
        Statement statement = CapturePath(new() { Path = "[type:task]", Query = QueryText, MinSimilarity = 0.5f, Count = 10 });

        MatchCollection laterals = LateralPattern.Matches(statement.Text);
        Assert.Multiple(() => {
            Assert.That(laterals.Select(m => m.Groups["alias"].Value), Is.EqualTo(new[] { "q", "sim" }));
            Assert.That(laterals.Select(m => statement.OffsetValue(m.Groups["offset"].Value)), Is.All.EqualTo(0L));
            Assert.That(Count(statement.Text, "<=>"), Is.EqualTo(1));
            Assert.That(statement.Parameters.Count(p => Equals(p, QueryText)), Is.EqualTo(1));
            Assert.That(statement.Where, Does.Contain("nodetype"));
        });
    }
}
