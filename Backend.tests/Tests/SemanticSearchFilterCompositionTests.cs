using System.Linq;
using System.Linq.Expressions;
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
///   <item>SQL-shape assertions (SF1–SF6): construct a <see cref="NodeService"/> and invoke
///         the production <see cref="NodeService.ApplySemanticSearch"/> method directly.
///         Build the filter predicate via <see cref="NodeService.GenerateFilter"/> (same path
///         as <see cref="NodeService.ListPaged"/>), pass it <c>ref</c> to
///         <c>ApplySemanticSearch</c>, then call <c>operation.Where(predicate?.Content)</c>
///         once.  Assert that the rendered WHERE clause contains BOTH the filter predicate AND
///         the embedding IS NOT NULL predicate in a single clause.
///
///         Load-bearing proof (per DiVoid #275): reverting ApplySemanticSearch to issue a
///         second <c>operation.Where(n =&gt; n.Embedding != null)</c> call causes the final
///         caller <c>operation.Where(predicate?.Content)</c> to overwrite the IS NOT NULL
///         clause, making SF1–SF6 fail on the IS NOT NULL assertion.  Restoring the fix
///         makes them pass again.</item>
///
///   <item>SF7a–d (guard tests, capability disabled): verify the isSemantic guard fires before
///         predicate composition is reached.  These pin guard ordering, not the composition fix.
///         They are smoke tests only — failure would produce NullReferenceException, not the
///         expected SemanticSearchUnavailableException.</item>
/// </list>
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
    /// creates a <see cref="NodeService"/> wired to <paramref name="em"/> with the
    /// enabled embedding capability (no real Postgres needed — we only build the
    /// operation tree and render SQL via <see cref="OperationPreparator"/>).
    /// </summary>
    static NodeService CreateService(IEntityManager em) => new(em, EnabledCapability);

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
    // Load-bearing proof:
    //   Revert ApplySemanticSearch to call operation.Where(n => n.Embedding != null) directly.
    //   The caller's final operation.Where(predicate?.Content) then overwrites that clause,
    //   leaving only the type predicate in SQL — IS NOT NULL assertion fails.
    //   Restoring the fix (AND into ref predicate) makes both assertions pass.
    // -----------------------------------------------------------------------

    [Test]
    public void SF1_TypeFilter_WithQuery_WhereContainsBothTypePredicateAndEmbeddingIsNotNull()
    {
        IEntityManager em = CreatePostgresEntityManager();
        NodeService service = CreateService(em);

        NodeFilter filter = new() { Query = "hivemind protocol", Type = ["task"], Count = 10 };
        NodeMapper mapper = new(filter);
        filter.Fields = [.. mapper.DefaultListFields, "similarity"];

        LoadOperation<Node> operation = mapper.CreateOperation(em, filter.Fields);
        operation.ApplyFilter(filter, mapper);

        // build filter predicate exactly as ListPaged does (via production GenerateFilter)
        Expression<Func<Node, bool>>? generated = service.GenerateFilter(filter);
        PredicateExpression<Node>? predicate = generated != null
            ? new PredicateExpression<Node>(generated)
            : null;

        // invoke the production method under test — must AND embedding IS NOT NULL into predicate
        service.ApplySemanticSearch(operation, filter, mapper, ref predicate);

        // single Where() call — mirrors the ListPaged caller contract
        operation.Where(predicate?.Content);

        string sql = RenderSql(operation);
        int whereIndex = sql.IndexOf("WHERE", System.StringComparison.OrdinalIgnoreCase);
        Assert.That(whereIndex, Is.GreaterThan(-1), "SQL must contain a WHERE clause");
        string whereSection = sql[whereIndex..];

        Assert.Multiple(() => {
            Assert.That(whereSection, Does.Contain("IS NOT NULL").IgnoreCase,
                "SF1: WHERE clause must contain IS NOT NULL (embedding predicate) when query= is present; " +
                "absent means ApplySemanticSearch issued a second Where() call (reverted bug)");
            Assert.That(whereSection, Does.Contain("nodetype").IgnoreCase.Or.Contain("type_id").IgnoreCase,
                "SF1: WHERE clause must contain the type subquery; " +
                "absent means the type filter was wiped by a second Where() call (bug #703)");
        });
    }

    // -----------------------------------------------------------------------
    // SF2 — status filter + query: WHERE must contain both status predicate and IS NOT NULL
    //
    // Load-bearing proof: same as SF1.
    // Expected failure on regression: status predicate absent from WHERE.
    // -----------------------------------------------------------------------

    [Test]
    public void SF2_StatusFilter_WithQuery_WhereContainsBothStatusPredicateAndEmbeddingIsNotNull()
    {
        IEntityManager em = CreatePostgresEntityManager();
        NodeService service = CreateService(em);

        NodeFilter filter = new() { Query = "open tasks", Status = ["open"], Count = 10 };
        NodeMapper mapper = new(filter);
        filter.Fields = [.. mapper.DefaultListFields, "similarity"];

        LoadOperation<Node> operation = mapper.CreateOperation(em, filter.Fields);
        operation.ApplyFilter(filter, mapper);

        Expression<Func<Node, bool>>? generated = service.GenerateFilter(filter);
        PredicateExpression<Node>? predicate = generated != null
            ? new PredicateExpression<Node>(generated)
            : null;

        service.ApplySemanticSearch(operation, filter, mapper, ref predicate);
        operation.Where(predicate?.Content);

        string sql = RenderSql(operation);
        int whereIndex = sql.IndexOf("WHERE", System.StringComparison.OrdinalIgnoreCase);
        Assert.That(whereIndex, Is.GreaterThan(-1), "SQL must contain a WHERE clause");
        string whereSection = sql[whereIndex..];

        Assert.Multiple(() => {
            Assert.That(whereSection, Does.Contain("IS NOT NULL").IgnoreCase,
                "SF2: WHERE clause must contain IS NOT NULL (embedding predicate) when query= is present");
            Assert.That(whereSection, Does.Contain("status").IgnoreCase,
                "SF2: WHERE clause must contain the status predicate; " +
                "absent means the status filter was wiped by the second Where() call (bug #703)");
        });
    }

    // -----------------------------------------------------------------------
    // SF3 — linkedto filter + query: WHERE must contain both link predicate and IS NOT NULL
    //
    // Load-bearing proof: same as SF1.
    // Expected failure on regression: link subquery absent from WHERE.
    // -----------------------------------------------------------------------

    [Test]
    public void SF3_LinkedToFilter_WithQuery_WhereContainsBothLinkPredicateAndEmbeddingIsNotNull()
    {
        IEntityManager em = CreatePostgresEntityManager();
        NodeService service = CreateService(em);

        long[] linkedTo = [42L];
        NodeFilter filter = new() { Query = "auth design", LinkedTo = linkedTo, Count = 10 };
        NodeMapper mapper = new(filter);
        filter.Fields = [.. mapper.DefaultListFields, "similarity"];

        LoadOperation<Node> operation = mapper.CreateOperation(em, filter.Fields);
        operation.ApplyFilter(filter, mapper);

        // GenerateFilter does not handle LinkedTo — it is handled separately via
        // BuildLinkedToFilter (internal, not exposed).  Build the linkedto predicate
        // using the UNION-of-two-directions shape (same path as SQLite fallback in
        // BuildLinkedToFilter) to get a non-null starting predicate.
        LoadOperation<NodeLink> linkOp = em.Load<NodeLink>(l => l.SourceId)
                                            .Where(l => l.SourceId.In(linkedTo) || l.TargetId.In(linkedTo))
                                            .Union(em.Load<NodeLink>(n => n.TargetId)
                                                     .Where(l => l.SourceId.In(linkedTo) || l.TargetId.In(linkedTo)));
        PredicateExpression<Node> predicate = new PredicateExpression<Node>(n => n.Id.In(linkOp) && !n.Id.In(linkedTo));

        service.ApplySemanticSearch(operation, filter, mapper, ref predicate);
        operation.Where(predicate?.Content);

        string sql = RenderSql(operation);
        int whereIndex = sql.IndexOf("WHERE", System.StringComparison.OrdinalIgnoreCase);
        Assert.That(whereIndex, Is.GreaterThan(-1), "SQL must contain a WHERE clause");
        string whereSection = sql[whereIndex..];

        Assert.Multiple(() => {
            Assert.That(whereSection, Does.Contain("IS NOT NULL").IgnoreCase,
                "SF3: WHERE clause must contain IS NOT NULL (embedding predicate) when query= is present");
            Assert.That(whereSection, Does.Contain("nodelink").IgnoreCase.Or.Contain("source_id").IgnoreCase,
                "SF3: WHERE clause must contain the link subquery; " +
                "absent means the linkedto filter was wiped by the second Where() call (bug #703)");
        });
    }

    // -----------------------------------------------------------------------
    // SF4 — id filter + query: WHERE must contain both id IN clause and IS NOT NULL
    //
    // Load-bearing proof: same as SF1.
    // Expected failure on regression: id IN clause absent.
    // -----------------------------------------------------------------------

    [Test]
    public void SF4_IdFilter_WithQuery_WhereContainsBothIdPredicateAndEmbeddingIsNotNull()
    {
        IEntityManager em = CreatePostgresEntityManager();
        NodeService service = CreateService(em);

        long[] ids = [1L, 2L, 3L];
        NodeFilter filter = new() { Query = "some query", Id = ids, Count = 10 };
        NodeMapper mapper = new(filter);
        filter.Fields = [.. mapper.DefaultListFields, "similarity"];

        LoadOperation<Node> operation = mapper.CreateOperation(em, filter.Fields);
        operation.ApplyFilter(filter, mapper);

        Expression<Func<Node, bool>>? generated = service.GenerateFilter(filter);
        PredicateExpression<Node>? predicate = generated != null
            ? new PredicateExpression<Node>(generated)
            : null;

        service.ApplySemanticSearch(operation, filter, mapper, ref predicate);
        operation.Where(predicate?.Content);

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
                "absent means the id filter was wiped by the second Where() call (bug #703)");
        });
    }

    // -----------------------------------------------------------------------
    // SF5 — name filter + query: WHERE must contain both name predicate and IS NOT NULL
    //
    // Load-bearing proof: same as SF1.
    // Expected failure on regression: name IN clause absent.
    // -----------------------------------------------------------------------

    [Test]
    public void SF5_NameFilter_WithQuery_WhereContainsBothNamePredicateAndEmbeddingIsNotNull()
    {
        IEntityManager em = CreatePostgresEntityManager();
        NodeService service = CreateService(em);

        string[] names = ["The Hivemind Protocol", "Agents"];
        NodeFilter filter = new() { Query = "protocol", Name = names, Count = 10 };
        NodeMapper mapper = new(filter);
        filter.Fields = [.. mapper.DefaultListFields, "similarity"];

        LoadOperation<Node> operation = mapper.CreateOperation(em, filter.Fields);
        operation.ApplyFilter(filter, mapper);

        Expression<Func<Node, bool>>? generated = service.GenerateFilter(filter);
        PredicateExpression<Node>? predicate = generated != null
            ? new PredicateExpression<Node>(generated)
            : null;

        service.ApplySemanticSearch(operation, filter, mapper, ref predicate);
        operation.Where(predicate?.Content);

        string sql = RenderSql(operation);
        int whereIndex = sql.IndexOf("WHERE", System.StringComparison.OrdinalIgnoreCase);
        Assert.That(whereIndex, Is.GreaterThan(-1), "SQL must contain a WHERE clause");
        string whereSection = sql[whereIndex..];

        Assert.Multiple(() => {
            Assert.That(whereSection, Does.Contain("IS NOT NULL").IgnoreCase,
                "SF5: WHERE clause must contain IS NOT NULL (embedding predicate) when query= is present");
            Assert.That(whereSection, Does.Contain("name").IgnoreCase,
                "SF5: WHERE clause must contain the name predicate; " +
                "absent means the name filter was wiped by the second Where() call (bug #703)");
        });
    }

    // -----------------------------------------------------------------------
    // SF6 — path-query + query: terminal WHERE must contain both path predicate
    //       and IS NOT NULL (exercises the ComposeHops fix path)
    //
    // Load-bearing proof: same as SF1 but in ComposeHops.
    // Expected failure on regression: type predicate from the hop absent in terminal WHERE.
    // -----------------------------------------------------------------------

    [Test]
    public void SF6_PathQuery_WithQuery_TerminalWhereContainsBothHopPredicateAndEmbeddingIsNotNull()
    {
        IEntityManager em = CreatePostgresEntityManager();
        NodeService service = CreateService(em);

        NodePathFilter filter = new() { Path = "[type:task]", Query = "hivemind", Count = 10 };
        NodeMapper mapper = new(filter);
        filter.Fields = [.. mapper.DefaultListFields, "similarity"];

        // Construct the terminal operation tree exactly as ComposeHops does for a single-hop
        // path "[type:task]" with a non-empty Query.  For a single hop the terminal predicate
        // is the hop filter (type IN subquery) — replicate that here.
        LoadOperation<NodeType> typeSubquery = em.Load<NodeType>(t => t.Id)
                                                  .Where(t => t.Type.In(new[] { "task" }));
        PredicateExpression<Node> combined = new PredicateExpression<Node>(n => n.TypeId.In(typeSubquery));

        LoadOperation<Node> terminal = mapper.CreateOperation(em, filter.Fields);
        terminal.ApplyFilter(filter, mapper);

        // invoke the production method — must AND embedding IS NOT NULL into combined
        service.ApplySemanticSearch(terminal, filter, mapper, ref combined);
        terminal.Where(combined?.Content);

        string sql = RenderSql(terminal);
        int whereIndex = sql.IndexOf("WHERE", System.StringComparison.OrdinalIgnoreCase);
        Assert.That(whereIndex, Is.GreaterThan(-1), "SQL must contain a WHERE clause");
        string whereSection = sql[whereIndex..];

        Assert.Multiple(() => {
            Assert.That(whereSection, Does.Contain("IS NOT NULL").IgnoreCase,
                "SF6: terminal WHERE clause must contain IS NOT NULL when query= is present; " +
                "absent means ApplySemanticSearch issued a second Where() (reverted ComposeHops bug)");
            Assert.That(whereSection, Does.Contain("nodetype").IgnoreCase.Or.Contain("type_id").IgnoreCase,
                "SF6: terminal WHERE clause must contain the hop type subquery; " +
                "absent means the path filter was wiped by a second Where() call in ComposeHops (bug #703)");
        });
    }

    // -----------------------------------------------------------------------
    // SF7a–d — guard tests (capability disabled).
    //
    // These tests pin the guard ordering inside ListPaged / ListPagedByPath:
    //   the isSemantic && !embeddingCapability.IsEnabled guard fires BEFORE
    //   predicate composition is reached, so these never exercise ApplySemanticSearch.
    //
    // They are smoke tests: if the guard were removed or moved after composition,
    // the exception type would change from SemanticSearchUnavailableException to
    // NullReferenceException (or a DB error), and these tests would catch that.
    // -----------------------------------------------------------------------

    [Test]
    public void SF7a_ListPaged_TypeFilter_WithQuery_OnSqlite_ThrowsCorrectGuardException()
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
