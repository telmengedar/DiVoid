using System;
using System.Collections.Generic;
using System.Globalization;
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
/// pins the SQL shape of multi-query semantic search and the unchanged shape of single-query search, as sent to Postgres
/// </summary>
[TestFixture, Parallelizable]
public class SemanticSearchMultiQuerySqlShapeTests
{
    const string Model = "gemini-embedding-001";

    const string UnitAListBaseline = @"SELECT node.""id"" , type.""type"" , node.""name"" , node.""status"" , node.""severity"" , node.""refinement"" , node.""rootnodeid"" , node.""contenttype"" , node.""ownerid"" , node.""access"" , node.""created"" , node.""lastupdate"" , sim.""similarity"", COUNT(*) OVER() AS __window FROM node AS node INNER JOIN nodetype AS type ON node.""typeid"" = type.""id"" INNER JOIN LATERAL ( SELECT subvector ( ( embedding ( gemini-embedding-001 , alpha ) ::vector ) , 1 , 768 ) AS v OFFSET 0 ) AS q ON TRUE INNER JOIN LATERAL ( SELECT ( 1 - CAST( q.""v"" <=> ( node.""embedding"" ::vector ) AS FLOAT) ) AS similarity OFFSET 0 ) AS sim ON TRUE WHERE node.""typeid"" = ANY( SELECT ""id"" FROM nodetype WHERE ""type"" = ANY( System.String[] ) ) AND node.""embedding"" IS NOT NULL AND sim.""similarity"" >= 0.5 ORDER BY sim.""similarity"" DESC , node.""id"" LIMIT 10";

    const string UnitAPathBaseline = @"SELECT node.""id"" , type.""type"" , node.""name"" , node.""status"" , node.""severity"" , node.""refinement"" , node.""rootnodeid"" , node.""contenttype"" , node.""ownerid"" , node.""access"" , node.""created"" , node.""lastupdate"" , sim.""similarity"", COUNT(*) OVER() AS __window FROM node AS node INNER JOIN nodetype AS type ON node.""typeid"" = type.""id"" INNER JOIN LATERAL ( SELECT subvector ( ( embedding ( gemini-embedding-001 , alpha ) ::vector ) , 1 , 768 ) AS v OFFSET 0 ) AS q ON TRUE INNER JOIN LATERAL ( SELECT ( 1 - CAST( q.""v"" <=> ( node.""embedding"" ::vector ) AS FLOAT) ) AS similarity OFFSET 0 ) AS sim ON TRUE WHERE node.""typeid"" = ANY( SELECT ""id"" FROM nodetype WHERE ""type"" = ANY( System.String[] ) ) AND node.""embedding"" IS NOT NULL AND sim.""similarity"" >= 0.5 ORDER BY sim.""similarity"" DESC , node.""id"" LIMIT 10";

    const string UnitAFieldsBaseline = @"SELECT node.""id"" , node.""name"", COUNT(*) OVER() AS __window FROM node AS node INNER JOIN nodetype AS type ON node.""typeid"" = type.""id"" INNER JOIN LATERAL ( SELECT subvector ( ( embedding ( gemini-embedding-001 , alpha ) ::vector ) , 1 , 768 ) AS v OFFSET 0 ) AS q ON TRUE INNER JOIN LATERAL ( SELECT ( 1 - CAST( q.""v"" <=> ( node.""embedding"" ::vector ) AS FLOAT) ) AS similarity OFFSET 0 ) AS sim ON TRUE WHERE node.""embedding"" IS NOT NULL ORDER BY sim.""similarity"" DESC , node.""id"" LIMIT 10";

    sealed class StatementCaptured(string sql, object[] parameters) : Exception
    {
        public string Sql { get; } = sql;

        public object[] Parameters { get; } = parameters;
    }

    sealed record Statement(string Sql, object[] Parameters)
    {
        public string Text => Regex.Replace(Sql, @"\s+", " ");

        public string Where => Text[Text.IndexOf(" WHERE ", StringComparison.Ordinal)..];

        public string SelectList => Text[..Text.IndexOf(" FROM ", StringComparison.Ordinal)];

        public string Resolved => Regex.Replace(Text, @"@(\d+)", m => Convert.ToString(Parameters[int.Parse(m.Groups[1].Value) - 1], CultureInfo.InvariantCulture) ?? "NULL");

        public string Laterals
        {
            get {
                string resolved = Resolved;
                int start = resolved.IndexOf(" INNER JOIN LATERAL ", StringComparison.Ordinal);
                int end = resolved.IndexOf(" WHERE ", StringComparison.Ordinal);
                return resolved[start..end];
            }
        }

        public int EmbeddingCalls => Regex.Matches(Text, @"(?<!"")\bembedding\s*\(").Count;
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
        StatementCaptured captured = Assert.ThrowsAsync<StatementCaptured>(() => CreateService().ListPaged(filter, callerId: 0, isAdmin: true))!;
        return new(captured.Sql, captured.Parameters);
    }

    static Statement CapturePath(NodePathFilter filter)
    {
        StatementCaptured captured = Assert.ThrowsAsync<StatementCaptured>(() => CreateService().ListPagedByPath(filter, callerId: 0, isAdmin: true, CancellationToken.None))!;
        return new(captured.Sql, captured.Parameters);
    }

    static int Count(string text, string needle) => Regex.Matches(text, Regex.Escape(needle)).Count;

    static string NormalizedEmbedding(string query)
        => $"l2_normalize ( subvector ( ( embedding ( {Model} , {query} ) ::vector ) , 1 , 768 ) )";

    static string ExpectedLaterals(string sum, int queryCount)
        => " INNER JOIN LATERAL ( SELECT qs.\"v\" AS v , ( CAST( vector_norm ( qs.\"v\" ) AS FLOAT) / " + queryCount + " ) AS scale"
           + " FROM ( SELECT " + sum + " AS v OFFSET 0 ) AS qs OFFSET 0 ) AS q ON TRUE"
           + " INNER JOIN LATERAL ( SELECT ( ( 1 - CAST( q.\"v\" <=> ( node.\"embedding\" ::vector ) AS FLOAT) ) * q.\"scale\" ) AS similarity OFFSET 0 ) AS sim ON TRUE";

    static string TwoQuerySum => $"vector_add ( {NormalizedEmbedding("alpha")} , {NormalizedEmbedding("beta")} )";

    [Test, Parallelizable]
    [Description("DiVoid #16138: a single query renders the statement of the single-query path with the query vector cut to the column dimension")]
    public void SingleQuery_ListStatement_MatchesUnitABaseline()
    {
        Statement statement = CaptureList(new() { Query = "alpha", MinSimilarity = 0.5f, Type = ["task"], Count = 10 });

        Assert.That(statement.Resolved, Is.EqualTo(UnitAListBaseline));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16138: a single query in path mode renders the statement of the single-query path with the query vector cut to the column dimension")]
    public void SingleQuery_PathStatement_MatchesUnitABaseline()
    {
        Statement statement = CapturePath(new() { Path = "[type:task]", Query = "alpha", MinSimilarity = 0.5f, Count = 10 });

        Assert.That(statement.Resolved, Is.EqualTo(UnitAPathBaseline));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16138: a single query without floor and with an explicit field list renders the unit A statement")]
    public void SingleQuery_ExplicitFieldsStatement_MatchesUnitABaseline()
    {
        Statement statement = CaptureList(new() { Query = "alpha", Count = 10, Fields = ["id", "name"] });

        Assert.That(statement.Resolved, Is.EqualTo(UnitAFieldsBaseline));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16138: a one-element queries list takes the single-query path and renders the same statement as query")]
    public void SingleElementQueries_RendersSameSqlAsQuery()
    {
        Statement viaQueries = CaptureList(new() { Queries = ["alpha"], MinSimilarity = 0.5f, Type = ["task"], Count = 10 });

        Assert.That(viaQueries.Resolved, Is.EqualTo(UnitAListBaseline));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16138: the reported value is the exact mean, (1 - cosine) times the norm of the summed vector divided by the query count, with the grouping spelled out")]
    public void TwoQueries_RenderedSql_ComputesMeanWithExplicitGrouping()
    {
        Statement statement = CaptureList(new() { Queries = ["alpha", "beta"], Count = 10 });

        Assert.That(statement.Laterals, Is.EqualTo(ExpectedLaterals(TwoQuerySum, 2)));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16138: the sum is a left fold over every query and the divisor follows the query count")]
    public void ThreeQueries_RenderedSql_FoldsAllVectorsAndDividesByThree()
    {
        Statement statement = CaptureList(new() { Queries = ["alpha", "beta", "gamma"], Count = 10 });
        string sum = $"vector_add ( vector_add ( {NormalizedEmbedding("alpha")} , {NormalizedEmbedding("beta")} ) , {NormalizedEmbedding("gamma")} )";

        Assert.That(statement.Laterals, Is.EqualTo(ExpectedLaterals(sum, 3)));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16138: every query text is embedded exactly once per statement, however often the floor, order and projection read the result")]
    public void TwoQueriesWithFloor_EachQueryEmbeddedExactlyOnce()
    {
        Statement statement = CaptureList(new() { Queries = ["alpha", "beta"], MinSimilarity = 0.5f, Count = 10 });

        Assert.Multiple(() => {
            Assert.That(statement.EmbeddingCalls, Is.EqualTo(2));
            Assert.That(statement.Parameters.Count(p => Equals(p, "alpha")), Is.EqualTo(1));
            Assert.That(statement.Parameters.Count(p => Equals(p, "beta")), Is.EqualTo(1));
            Assert.That(Count(statement.Text, "<=>"), Is.EqualTo(1));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16138: ten queries are ten embedding calls, not sixty")]
    public void TenQueriesWithFloor_RenderTenEmbeddingCalls()
    {
        Statement statement = CaptureList(new() { Queries = [.. Enumerable.Range(1, 10).Select(i => $"query{i}")], MinSimilarity = 0.5f, Count = 10 });

        Assert.That(statement.EmbeddingCalls, Is.EqualTo(10));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16138: repeated query strings are kept as given, so the mean is over the supplied list")]
    public void DuplicateQueries_AreNotDeduplicated()
    {
        Statement statement = CaptureList(new() { Queries = ["same", "same"], Count = 10 });
        string sum = $"vector_add ( {NormalizedEmbedding("same")} , {NormalizedEmbedding("same")} )";

        Assert.That(statement.Laterals, Is.EqualTo(ExpectedLaterals(sum, 2)));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16138: the summed vector is fenced in its own level and every semantic level carries OFFSET 0")]
    public void TwoQueries_AllThreeLevelsCarryOffsetZero()
    {
        Statement statement = CaptureList(new() { Queries = ["alpha", "beta"], MinSimilarity = 0.5f, Count = 10 });

        Assert.That(Count(statement.Resolved, " OFFSET 0 )"), Is.EqualTo(3));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16138: floor and ORDER BY read the precomputed similarity column and the query vector is not projected into the outer select")]
    public void TwoQueries_FloorAndOrderBy_ReferenceSimColumn()
    {
        Statement statement = CaptureList(new() { Queries = ["alpha", "beta"], MinSimilarity = 0.5f, Type = ["task"], Count = 10 });

        Assert.Multiple(() => {
            Assert.That(statement.Where, Does.Contain("nodetype"));
            Assert.That(statement.Where, Does.Contain("node.\"embedding\" IS NOT NULL"));
            Assert.That(statement.Where, Does.Contain("sim.\"similarity\" >="));
            Assert.That(statement.Where, Does.Contain("ORDER BY sim.\"similarity\" DESC , node.\"id\""));
            Assert.That(statement.Where, Does.Not.Contain("<=>"));
            Assert.That(statement.SelectList, Does.Not.Contain("q.\"v\""));
            Assert.That(statement.SelectList, Does.Not.Contain("scale"));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16138: with the capability enabled, fields=similarity with only queries supplied reaches the database and projects the sim column")]
    public void TwoQueries_SimilarityFieldRequested_ProjectsSimColumn()
    {
        Statement statement = CaptureList(new() { Queries = ["alpha", "beta"], Fields = ["id", "similarity"], Count = 10 });

        Assert.Multiple(() => {
            Assert.That(statement.SelectList, Does.Contain("sim.\"similarity\""));
            Assert.That(statement.Laterals, Is.EqualTo(ExpectedLaterals(TwoQuerySum, 2)));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16138: path mode with several queries keeps the hop predicate beside the embedding predicate and renders the same sub-selects")]
    public void PathQuery_TwoQueries_TerminalWhereKeepsHopPredicateAndEmbeddingNotNull()
    {
        Statement statement = CapturePath(new() { Path = "[type:task]", Queries = ["alpha", "beta"], MinSimilarity = 0.5f, Count = 10 });

        Assert.Multiple(() => {
            Assert.That(statement.Laterals, Is.EqualTo(ExpectedLaterals(TwoQuerySum, 2)));
            Assert.That(statement.Where, Does.Contain("nodetype"));
            Assert.That(statement.Where, Does.Contain("node.\"embedding\" IS NOT NULL"));
            Assert.That(statement.Where, Does.Contain("sim.\"similarity\" >="));
        });
    }
}
