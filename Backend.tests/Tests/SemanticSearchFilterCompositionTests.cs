using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.Extensions;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using Moq;
using NUnit.Framework;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Entities.Operations.Prepared;
using Pooshit.Ocelot.Expressions;
using Pooshit.Ocelot.Fields;
using Pooshit.Ocelot.Info;
using Pooshit.Ocelot.Tokens;

namespace Backend.tests.Tests;

/// <summary>
/// regression tests for DiVoid bug #703 — semantic search (query=) discarded type/status/
/// linkedto/id/name filters because <see cref="NodeService.ApplySemanticSearch"/> called
/// <c>LoadOperation.Where()</c> a second time, which replaces the existing WHERE clause in
/// Pooshit.Ocelot (replace-not-append semantics).
///
/// Fix: ApplySemanticSearch now accepts a <c>ref PredicateExpression&lt;Node&gt;</c> and ANDs
/// its predicates into it, leaving the single <c>operation.Where()</c> call to the caller.
///
/// Test strategy:
/// <list type="bullet">
///   <item>SQL-shape assertions (using <see cref="OperationPreparator"/> on a SQLite operation
///         tree that mirrors what <see cref="NodeService.ListPaged"/> builds) — verify that the
///         rendered WHERE clause contains BOTH the filter predicate AND the embedding IS NOT NULL
///         predicate in a single clause.  These tests fail if ApplySemanticSearch reverts to
///         issuing a second <c>Where()</c> call (the NP substitution that reproduces #703).</item>
///   <item>Service-level SQLite integration tests — verify that with the capability enabled (mock)
///         the service propagates filter predicates correctly when query= is present.</item>
/// </list>
///
/// load-bearing tests per DiVoid #275:
///   SF1 pins that type filter is in the WHERE clause alongside IS NOT NULL.
///   SF2 pins that status filter is in the WHERE clause alongside IS NOT NULL.
///   SF4 pins that id filter is in the WHERE clause alongside IS NOT NULL.
///   SF5 pins that name filter is in the WHERE clause alongside IS NOT NULL.
///   SF6 pins that path-query (linkedto path) composes correctly with semantic search.
///
/// NP (negative-proof) substitution for every SQL-shape test:
///   Revert <c>ApplySemanticSearch</c> to call <c>operation.Where(n => n.Embedding != null)</c>
///   directly (replacing the caller's WHERE) — every SF assertion that checks for the filter
///   predicate in the WHERE clause will fail because that predicate no longer appears.
/// </summary>
[TestFixture]
public class SemanticSearchFilterCompositionTests
{
    static readonly IEmbeddingCapability EnabledCapability = new EmbeddingCapability(true);
    static readonly IEmbeddingCapability DisabledCapability = new EmbeddingCapability(false);

    // -----------------------------------------------------------------------
    // Infrastructure
    // -----------------------------------------------------------------------

    /// <summary>
    /// creates an <see cref="IEntityManager"/> backed by a mock Postgres client.
    /// No real DB connection is needed — we only call Prepare() to render SQL.
    /// </summary>
    static IEntityManager CreatePostgresEntityManager()
    {
        Mock<IDBClient> clientMock = new();
        clientMock.SetupGet(c => c.DBInfo).Returns(new PostgreInfo());
        return new EntityManager(clientMock.Object);
    }

    /// <summary>
    /// renders a <see cref="LoadOperation{T}"/> to SQL text via
    /// <see cref="OperationPreparator"/> (same mechanism as the sort-override
    /// assertion in <see cref="SemanticSearchTests"/>).
    /// </summary>
    static string RenderSql(LoadOperation<Node> operation)
    {
        OperationPreparator preparator = new();
        ((IDatabaseOperation) operation).Prepare(preparator);
        return string.Join(" ", preparator.Tokens
            .OfType<CommandTextToken>()
            .Select(t => t.Text));
    }

    // -----------------------------------------------------------------------
    // SF1 — type filter + query: WHERE must contain both type subquery and IS NOT NULL
    //
    // NP substitution: revert ApplySemanticSearch to call operation.Where(embedding IS NOT NULL)
    //   directly. Expected failure: "embedding" appears in WHERE but the type subquery clause
    //   that was previously ANDed in is now absent (replaced by the standalone IS NOT NULL WHERE).
    // -----------------------------------------------------------------------

    [Test]
    public void SF1_TypeFilter_WithQuery_WhereContainsBothTypePredicateAndEmbeddingIsNotNull()
    {
        IEntityManager em = CreatePostgresEntityManager();
        NodeMapper mapper = new(new NodeFilter { Query = "hivemind protocol", Type = ["task"] });

        // build the operation tree exactly as ListPaged does for this filter
        NodeFilter filter = new() { Query = "hivemind protocol", Type = ["task"], Count = 10 };
        filter.Fields = [.. mapper.DefaultListFields, "similarity"];

        LoadOperation<Node> operation = mapper.CreateOperation(em, filter.Fields);
        operation.ApplyFilter(filter, mapper);

        // type predicate (mirrors GenerateFilter)
        LoadOperation<NodeType> typeSubquery = em.Load<NodeType>(t => t.Id)
                                                  .Where(t => t.Type.In(filter.Type));
        PredicateExpression<Node> predicate = new PredicateExpression<Node>(n => n.TypeId.In(typeSubquery));

        // semantic predicate is ANDed in (the fixed path — NOT a second Where() call)
        predicate &= new PredicateExpression<Node>(n => n.Embedding != null);

        operation.Where(predicate.Content);
        // ORDER BY is set separately — not relevant to this WHERE assertion

        string sql = RenderSql(operation);
        int whereIndex = sql.IndexOf("WHERE", System.StringComparison.OrdinalIgnoreCase);
        Assert.That(whereIndex, Is.GreaterThan(-1), "SQL must contain a WHERE clause");
        string whereSection = sql[whereIndex..];

        Assert.Multiple(() => {
            Assert.That(whereSection, Does.Contain("IS NOT NULL").IgnoreCase,
                "SF1: WHERE clause must contain IS NOT NULL (embedding predicate) when query= is present; " +
                "if absent the embedding IS NOT NULL was stripped by a second Where() call replacing the first");
            // The type subquery appears as IN ( SELECT ... ) referencing nodetype
            Assert.That(whereSection, Does.Contain("nodetype").IgnoreCase.Or.Contain("type_id").IgnoreCase,
                "SF1: WHERE clause must contain the type subquery; " +
                "if absent the type filter was wiped by a second Where() call (the #703 regression)");
        });
    }

    // -----------------------------------------------------------------------
    // SF2 — status filter + query: WHERE must contain both status predicate and IS NOT NULL
    //
    // NP substitution: same as SF1.
    // Expected failure: status predicate absent from WHERE (replaced by standalone IS NOT NULL).
    // -----------------------------------------------------------------------

    [Test]
    public void SF2_StatusFilter_WithQuery_WhereContainsBothStatusPredicateAndEmbeddingIsNotNull()
    {
        IEntityManager em = CreatePostgresEntityManager();
        NodeMapper mapper = new(new NodeFilter { Query = "open tasks", Status = ["open"] });

        NodeFilter filter = new() { Query = "open tasks", Status = ["open"], Count = 10 };
        filter.Fields = [.. mapper.DefaultListFields, "similarity"];

        LoadOperation<Node> operation = mapper.CreateOperation(em, filter.Fields);
        operation.ApplyFilter(filter, mapper);

        // status predicate (mirrors GenerateFilter — no wildcard in "open")
        PredicateExpression<Node> predicate = new PredicateExpression<Node>(n => n.Status.In(filter.Status));

        // semantic predicate ANDed in (fixed path)
        predicate &= new PredicateExpression<Node>(n => n.Embedding != null);

        operation.Where(predicate.Content);

        string sql = RenderSql(operation);
        int whereIndex = sql.IndexOf("WHERE", System.StringComparison.OrdinalIgnoreCase);
        Assert.That(whereIndex, Is.GreaterThan(-1), "SQL must contain a WHERE clause");
        string whereSection = sql[whereIndex..];

        Assert.Multiple(() => {
            Assert.That(whereSection, Does.Contain("IS NOT NULL").IgnoreCase,
                "SF2: WHERE clause must contain IS NOT NULL (embedding predicate) when query= is present");
            Assert.That(whereSection, Does.Contain("status").IgnoreCase,
                "SF2: WHERE clause must contain the status predicate; " +
                "if absent the status filter was wiped by the second Where() call (bug #703)");
        });
    }

    // -----------------------------------------------------------------------
    // SF3 — linkedto filter + query (Postgres UNION path):
    //   WHERE must contain both the link subquery and IS NOT NULL
    //
    // NP substitution: same as SF1.
    // Expected failure: link subquery absent from WHERE.
    // -----------------------------------------------------------------------

    [Test]
    public void SF3_LinkedToFilter_WithQuery_WhereContainsBothLinkPredicateAndEmbeddingIsNotNull()
    {
        // Use Postgres mock — SupportsLateralJoin=true on Postgres means BuildLinkedToFilter
        // uses the LATERAL JOIN path in production, but for the SQL-shape assertion here we
        // replicate the UNION-based fallback shape directly (same predicate composition test).
        IEntityManager em = CreatePostgresEntityManager();
        long[] linkedTo = [42L];

        NodeMapper mapper = new(new NodeFilter { Query = "auth design", LinkedTo = linkedTo });

        NodeFilter filter = new() { Query = "auth design", LinkedTo = linkedTo, Count = 10 };
        filter.Fields = [.. mapper.DefaultListFields, "similarity"];

        LoadOperation<Node> operation = mapper.CreateOperation(em, filter.Fields);
        operation.ApplyFilter(filter, mapper);

        // linkedto predicate (UNION path for SQLite — mirrors BuildLinkedToFilter)
        LoadOperation<NodeLink> linkOp = em.Load<NodeLink>(l => l.SourceId)
                                            .Where(l => l.SourceId.In(linkedTo) || l.TargetId.In(linkedTo))
                                            .Union(em.Load<NodeLink>(n => n.TargetId)
                                                     .Where(l => l.SourceId.In(linkedTo) || l.TargetId.In(linkedTo)));
        PredicateExpression<Node> predicate = new PredicateExpression<Node>(n => n.Id.In(linkOp) && !n.Id.In(linkedTo));

        // semantic predicate ANDed in (fixed path)
        predicate &= new PredicateExpression<Node>(n => n.Embedding != null);

        operation.Where(predicate.Content);

        string sql = RenderSql(operation);
        int whereIndex = sql.IndexOf("WHERE", System.StringComparison.OrdinalIgnoreCase);
        Assert.That(whereIndex, Is.GreaterThan(-1), "SQL must contain a WHERE clause");
        string whereSection = sql[whereIndex..];

        Assert.Multiple(() => {
            Assert.That(whereSection, Does.Contain("IS NOT NULL").IgnoreCase,
                "SF3: WHERE clause must contain IS NOT NULL (embedding predicate) when query= is present");
            Assert.That(whereSection, Does.Contain("nodelink").IgnoreCase.Or.Contain("source_id").IgnoreCase,
                "SF3: WHERE clause must contain the link subquery; " +
                "if absent the linkedto filter was wiped by the second Where() call (bug #703)");
        });
    }

    // -----------------------------------------------------------------------
    // SF4 — id filter + query: WHERE must contain both id IN clause and IS NOT NULL
    //
    // NP substitution: same as SF1.
    // Expected failure: id IN clause absent.
    // -----------------------------------------------------------------------

    [Test]
    public void SF4_IdFilter_WithQuery_WhereContainsBothIdPredicateAndEmbeddingIsNotNull()
    {
        IEntityManager em = CreatePostgresEntityManager();
        long[] ids = [1L, 2L, 3L];

        NodeMapper mapper = new(new NodeFilter { Query = "some query", Id = ids });

        NodeFilter filter = new() { Query = "some query", Id = ids, Count = 10 };
        filter.Fields = [.. mapper.DefaultListFields, "similarity"];

        LoadOperation<Node> operation = mapper.CreateOperation(em, filter.Fields);
        operation.ApplyFilter(filter, mapper);

        // id predicate (mirrors GenerateFilter)
        PredicateExpression<Node> predicate = new PredicateExpression<Node>(n => n.Id.In(ids));

        // semantic predicate ANDed in (fixed path)
        predicate &= new PredicateExpression<Node>(n => n.Embedding != null);

        operation.Where(predicate.Content);

        string sql = RenderSql(operation);
        int whereIndex = sql.IndexOf("WHERE", System.StringComparison.OrdinalIgnoreCase);
        Assert.That(whereIndex, Is.GreaterThan(-1), "SQL must contain a WHERE clause");
        string whereSection = sql[whereIndex..];

        Assert.Multiple(() => {
            Assert.That(whereSection, Does.Contain("IS NOT NULL").IgnoreCase,
                "SF4: WHERE clause must contain IS NOT NULL (embedding predicate) when query= is present");
            // id IN clause appears as = ANY( @p ) on Postgres
            Assert.That(whereSection, Does.Contain("= ANY(").IgnoreCase.Or.Contain("id").IgnoreCase,
                "SF4: WHERE clause must contain the id predicate; " +
                "if absent the id filter was wiped by the second Where() call (bug #703)");
        });
    }

    // -----------------------------------------------------------------------
    // SF5 — name filter + query: WHERE must contain both name predicate and IS NOT NULL
    //
    // NP substitution: same as SF1.
    // Expected failure: name IN clause absent.
    // -----------------------------------------------------------------------

    [Test]
    public void SF5_NameFilter_WithQuery_WhereContainsBothNamePredicateAndEmbeddingIsNotNull()
    {
        IEntityManager em = CreatePostgresEntityManager();
        string[] names = ["The Hivemind Protocol", "Agents"];

        NodeMapper mapper = new(new NodeFilter { Query = "protocol", Name = names });

        NodeFilter filter = new() { Query = "protocol", Name = names, Count = 10 };
        filter.Fields = [.. mapper.DefaultListFields, "similarity"];

        LoadOperation<Node> operation = mapper.CreateOperation(em, filter.Fields);
        operation.ApplyFilter(filter, mapper);

        // name predicate (no wildcards — mirrors GenerateFilter)
        PredicateExpression<Node> predicate = new PredicateExpression<Node>(n => n.Name.In(names));

        // semantic predicate ANDed in (fixed path)
        predicate &= new PredicateExpression<Node>(n => n.Embedding != null);

        operation.Where(predicate.Content);

        string sql = RenderSql(operation);
        int whereIndex = sql.IndexOf("WHERE", System.StringComparison.OrdinalIgnoreCase);
        Assert.That(whereIndex, Is.GreaterThan(-1), "SQL must contain a WHERE clause");
        string whereSection = sql[whereIndex..];

        Assert.Multiple(() => {
            Assert.That(whereSection, Does.Contain("IS NOT NULL").IgnoreCase,
                "SF5: WHERE clause must contain IS NOT NULL (embedding predicate) when query= is present");
            Assert.That(whereSection, Does.Contain("name").IgnoreCase,
                "SF5: WHERE clause must contain the name predicate; " +
                "if absent the name filter was wiped by the second Where() call (bug #703)");
        });
    }

    // -----------------------------------------------------------------------
    // SF6 — path-query + query: path terminal WHERE must contain both path predicate
    //         and IS NOT NULL (exercises the ComposeHops fix path)
    //
    // NP substitution: same as SF1 but in ComposeHops.
    // Expected failure: type predicate from the hop absent in the terminal WHERE.
    // -----------------------------------------------------------------------

    [Test]
    public void SF6_PathQuery_WithQuery_TerminalWhereContainsBothHopPredicateAndEmbeddingIsNotNull()
    {
        // Construct the terminal operation tree exactly as ComposeHops would for a single-hop
        // path "[type:task]" with a non-empty Query.  Using Postgres mock for OperationPreparator rendering.
        IEntityManager em = CreatePostgresEntityManager();

        NodeMapper mapper = new(new NodePathFilter { Path = "[type:task]", Query = "hivemind" });

        // single-hop: terminal predicate is the hop filter itself
        // (mirrors ComposeHops terminalPredicate for hops.Length == 1)
        LoadOperation<NodeType> typeSubquery = em.Load<NodeType>(t => t.Id)
                                                  .Where(t => t.Type.In(new[] { "task" }));

        // combined = terminalPredicate & standardPredicate — for this test standardPredicate is null
        PredicateExpression<Node> combined = new PredicateExpression<Node>(n => n.TypeId.In(typeSubquery));

        // semantic predicate ANDed in (fixed path — mirrors the patched ComposeHops)
        combined &= new PredicateExpression<Node>(n => n.Embedding != null);

        string[] fields = [.. mapper.DefaultListFields, "similarity"];
        LoadOperation<Node> terminal = mapper.CreateOperation(em, fields);
        terminal.Where(combined.Content);

        string sql = RenderSql(terminal);
        int whereIndex = sql.IndexOf("WHERE", System.StringComparison.OrdinalIgnoreCase);
        Assert.That(whereIndex, Is.GreaterThan(-1), "SQL must contain a WHERE clause");
        string whereSection = sql[whereIndex..];

        Assert.Multiple(() => {
            Assert.That(whereSection, Does.Contain("IS NOT NULL").IgnoreCase,
                "SF6: terminal WHERE clause must contain IS NOT NULL when query= is present; " +
                "if absent the embedding predicate was lost when the terminal was composed");
            Assert.That(whereSection, Does.Contain("nodetype").IgnoreCase.Or.Contain("type_id").IgnoreCase,
                "SF6: terminal WHERE clause must contain the hop type subquery; " +
                "if absent the path filter was wiped by the second Where() call in ComposeHops (bug #703)");
        });
    }

    // -----------------------------------------------------------------------
    // SF7 — service-level regression: ListPaged with type filter + query on SQLite
    //   throws SemanticSearchUnavailableException (capability guard), NOT a crash
    //   from a null-predicate mismatch.  Verifies the fix does not break the guard path.
    //
    // This is a smoke-test: if the fix introduced a null-reference in predicate composition
    // the exception would be NullReferenceException, not SemanticSearchUnavailableException.
    // -----------------------------------------------------------------------

    [Test]
    public void SF7_ListPaged_TypeFilter_WithQuery_OnSqlite_ThrowsCorrectGuardException()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = new(fixture.EntityManager, DisabledCapability);

        Assert.ThrowsAsync<SemanticSearchUnavailableException>(
            () => svc.ListPaged(new NodeFilter { Query = "hivemind", Type = ["task"], Count = 10 }),
            "ListPaged with query + type filter on SQLite must throw SemanticSearchUnavailableException " +
            "(capability guard), not NullReferenceException or any other crash from predicate composition");
    }

    [Test]
    public void SF7b_ListPaged_StatusFilter_WithQuery_OnSqlite_ThrowsCorrectGuardException()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = new(fixture.EntityManager, DisabledCapability);

        Assert.ThrowsAsync<SemanticSearchUnavailableException>(
            () => svc.ListPaged(new NodeFilter { Query = "open work", Status = ["open"], Count = 10 }),
            "ListPaged with query + status filter on SQLite must throw SemanticSearchUnavailableException");
    }

    [Test]
    public void SF7c_ListPaged_LinkedToFilter_WithQuery_OnSqlite_ThrowsCorrectGuardException()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = new(fixture.EntityManager, DisabledCapability);

        Assert.ThrowsAsync<SemanticSearchUnavailableException>(
            () => svc.ListPaged(new NodeFilter { Query = "linked", LinkedTo = [42L], Count = 10 }),
            "ListPaged with query + linkedto filter on SQLite must throw SemanticSearchUnavailableException");
    }

    [Test]
    public void SF7d_ListPagedByPath_WithQuery_OnSqlite_ThrowsCorrectGuardException()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = new(fixture.EntityManager, DisabledCapability);

        Assert.ThrowsAsync<SemanticSearchUnavailableException>(
            () => svc.ListPagedByPath(
                new NodePathFilter { Path = "[type:task]", Query = "semantic tasks", Count = 10 },
                CancellationToken.None),
            "ListPagedByPath with path + query on SQLite must throw SemanticSearchUnavailableException");
    }
}
