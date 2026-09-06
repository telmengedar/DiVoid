using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
/// pins the Postgres-dialect SQL shape of the linkedto filter, captured at the
/// <see cref="IDBClient.ReaderAsync"/> boundary production drives through
/// <see cref="NodeService.ListPaged"/>.
/// </summary>
[TestFixture, Parallelizable]
public class LinkedToSqlShapeTests
{
    static readonly IEmbeddingCapability DisabledCapability = new EmbeddingCapability(false);

    /// <summary>
    /// thrown once <see cref="IDBClient.ReaderAsync"/> has captured the SQL production sent it,
    /// short-circuiting the call before a live database is needed.
    /// </summary>
    class SqlCapturedException : Exception { }

    /// <summary>
    /// the parenthesized group immediately enclosing <c>FROM nodelink</c> in a captured
    /// linkedto command — the link subquery, isolated from the rest of the statement.
    /// </summary>
    static string ExtractLinkSubquery(string sql)
    {
        int contentStart = sql.IndexOf("FROM nodelink", StringComparison.Ordinal);
        Assert.That(contentStart, Is.GreaterThanOrEqualTo(0),
            "expected the captured SQL to contain a nodelink subquery");

        int openParen = sql.LastIndexOf('(', contentStart);
        Assert.That(openParen, Is.GreaterThanOrEqualTo(0),
            "expected the nodelink subquery to be enclosed in parentheses");

        int depth = 1;
        int i = openParen + 1;
        for (; i < sql.Length && depth > 0; i++)
        {
            if (sql[i] == '(') depth++;
            else if (sql[i] == ')') depth--;
        }
        Assert.That(depth, Is.EqualTo(0),
            "unbalanced parentheses while isolating the nodelink subquery");

        return sql.Substring(openParen + 1, i - openParen - 2);
    }

    [Test, Parallelizable]
    public void LinkedTo_PostgresDialect_UncorrelatedUnionShape_NoLateral()
    {
        Mock<IDBClient> clientMock = new();
        clientMock.SetupGet(c => c.DBInfo).Returns(new PostgreInfo());
        string? capturedSql = null;
        clientMock
            .Setup(c => c.ReaderAsync(It.IsAny<Transaction>(), It.IsAny<string>(), It.IsAny<IEnumerable<object>>(), It.IsAny<CancellationToken>()))
            .Callback<Transaction, string, IEnumerable<object>, CancellationToken>((_, command, _, _) => capturedSql = command)
            .ThrowsAsync(new SqlCapturedException());

        IEntityManager em = new EntityManager(clientMock.Object);
        NodeService service = new(em, DisabledCapability);
        NodeFilter filter = new() { LinkedTo = [1L, 2L, 3L, 4L, 5L], Count = 20 };

        Assert.ThrowsAsync<SqlCapturedException>(() => service.ListPaged(filter, callerId: 0, isAdmin: true));

        Assert.That(capturedSql, Is.Not.Null.And.Not.Empty,
            "SQL must be genuinely captured at the IDBClient.ReaderAsync boundary before any absence assertion can mean anything (#11140)");
        Assert.That(capturedSql, Does.Contain("nodelink").IgnoreCase,
            "captured SQL must carry the linkedto subquery — its absence would mean the capture point missed production's real query");

        string linkSubquery = ExtractLinkSubquery(capturedSql!);

        Assert.That(linkSubquery, Does.Contain("FROM nodelink"),
            "the isolated region must itself still contain the nodelink subquery — otherwise G2b below would pass vacuously on a collapsed fragment");

        Assert.Multiple(() => {
            Assert.That(capturedSql, Does.Not.Contain("LATERAL"),
                "G1: Postgres-dialect linkedto SQL must not contain a LATERAL join");
            Assert.That(capturedSql, Does.Contain("UNION ALL"),
                "G2a: linkedto SQL must contain the UNION ALL link subquery");
            Assert.That(linkSubquery, Does.Not.Match(@"\bnode\s*\."),
                "G2b: the first parenthesised group enclosing FROM nodelink must contain no occurrence of the lowercase token \"node\" immediately followed by \".\" (whitespace allowed between the two), in either operand position of a comparison");
        });
    }
}
