using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Http;
using System.Threading.Tasks;
using Backend.Extensions;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Moq;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Http;
using Pooshit.Json;
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
/// service-layer and HTTP-layer integration tests for <see cref="Node.RootNodeId"/>.
///
/// Covers create, patch, filter, noRootNodeId flag, OR-composition, wire-shape JSON
/// assertion, and a SQL-shape assertion confirming rootNodeId composes with semantic search.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.Fixtures)]
public class NodeRootNodeIdTests
{
    static readonly IEmbeddingCapability DisabledCapability = new EmbeddingCapability(false);

    static NodeService MakeService(DatabaseFixture fixture) => new(fixture.EntityManager, DisabledCapability);

    static Task<NodeDetails> Create(NodeService svc, string type = "task", string name = "Test node")
        => svc.CreateNode(new NodeDetails { Type = type, Name = name }, callerId: 0);

    static Task<NodeDetails> CreateWithRootNodeId(NodeService svc, long rootNodeId, string name = "Test node")
        => svc.CreateNode(new NodeDetails { Type = "task", Name = name, RootNodeId = rootNodeId }, callerId: 0);

    static async Task<List<NodeDetails>> CollectPage(AsyncPageResponseWriter<NodeDetails> writer)
    {
        byte[] buffer;
        using (MemoryStream ms = new())
        {
            await writer.Write(ms);
            buffer = ms.ToArray();
        }
        using MemoryStream readStream = new(buffer);
        string json = await new StreamReader(readStream).ReadToEndAsync();
        Page<NodeDetails> page = Json.Read<Page<NodeDetails>>(json);
        return page.Result?.ToList() ?? [];
    }

    /// <summary>
    /// Verifies that a <see cref="Node.RootNodeId"/> supplied on create survives
    /// a subsequent GET from the database — it is not merely echoed from the DTO.
    ///
    /// POSITIVE PROOF: with the column included in the INSERT Columns/Values list and
    /// in DefaultListFields + the mapper, the round-trip returns the original value.
    ///
    /// NEGATIVE PROOF: remove <c>n => n.RootNodeId</c> from the INSERT Columns call
    /// in CreateNode — the DB row stores NULL and GetNodeById returns null, failing the
    /// <c>Is.EqualTo(root.Id)</c> assertion.
    /// </summary>
    [Test]
    public async Task CreateNode_WithRootNodeId_RootNodeIdPersistedToDatabase()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails root = await Create(svc, name: "RootNode");
        NodeDetails child = await CreateWithRootNodeId(svc, root.Id, name: "ChildNode");

        Assert.That(child.Id, Is.GreaterThan(0), "POST must return a valid id");

        NodeDetails fetched = await svc.GetNodeById(child.Id, callerId: 0, isAdmin: true);

        Assert.That(fetched.RootNodeId, Is.EqualTo(root.Id),
            "rootNodeId set on create must survive a subsequent GetNodeById (DB round-trip)");
    }

    /// <summary>
    /// Verifies that nodes created without a <see cref="Node.RootNodeId"/> have null
    /// after a GET — no server-side default is applied.
    ///
    /// POSITIVE PROOF: with the column nullable (no DefaultValue), the row stores NULL
    /// and GetNodeById returns null.
    ///
    /// NEGATIVE PROOF: add <c>[DefaultValue(0L)]</c> to <c>Node.RootNodeId</c> — the row
    /// stores 0, and null assertion fails with 0 returned.
    /// </summary>
    [Test]
    public async Task CreateNode_WithoutRootNodeId_RootNodeIdIsNullAfterGet()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await Create(svc, name: "NoRootNode");
        NodeDetails fetched = await svc.GetNodeById(node.Id, callerId: 0, isAdmin: true);

        Assert.That(fetched.RootNodeId, Is.Null,
            "nodes created without a rootNodeId must have null rootNodeId (no server-side default)");
    }

    /// <summary>
    /// Verifies that PATCH replace /RootNodeId updates the stored value.
    ///
    /// POSITIVE PROOF: with <c>[AllowPatch]</c> on <c>Node.RootNodeId</c>, the Patch
    /// extension routes the op and the updated value is returned.
    ///
    /// NEGATIVE PROOF: remove <c>[AllowPatch]</c> from <c>Node.RootNodeId</c> — the Patch
    /// extension throws <see cref="NotSupportedException"/> and the test fails.
    /// </summary>
    [Test]
    public async Task Patch_ReplaceRootNodeId_UpdatesField()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails root = await Create(svc, name: "Root");
        NodeDetails child = await Create(svc, name: "Child");

        NodeDetails patched = await svc.Patch(
            child.Id,
            [new PatchOperation { Op = "replace", Path = "/rootNodeId", Value = root.Id }],
            callerId: 0, isAdmin: true, CancellationToken.None);

        Assert.That(patched.RootNodeId, Is.EqualTo(root.Id),
            "PATCH replace /rootNodeId must update the stored value and return it in the response");
    }

    /// <summary>
    /// Verifies that PATCH replace /RootNodeId with a null value clears the field.
    ///
    /// POSITIVE PROOF: the column is nullable, so a null value in the patch op is stored
    /// as NULL and GetNodeById returns null.
    ///
    /// NEGATIVE PROOF: if the patch extension rejects null for nullable columns, the PATCH
    /// call throws instead of succeeding, and the null assertion is never reached.
    /// </summary>
    [Test]
    public async Task Patch_ReplaceRootNodeId_WithNull_ClearsValue()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails root = await Create(svc, name: "Root");
        NodeDetails child = await CreateWithRootNodeId(svc, root.Id, name: "Child");
        Assert.That(child.RootNodeId, Is.EqualTo(root.Id), "precondition: child must start with rootNodeId set");

        NodeDetails cleared = await svc.Patch(
            child.Id,
            [new PatchOperation { Op = "replace", Path = "/rootNodeId", Value = null }],
            callerId: 0, isAdmin: true, CancellationToken.None);

        Assert.That(cleared.RootNodeId, Is.Null,
            "PATCH replace /rootNodeId -> null must clear the stored value");
    }

    /// <summary>
    /// Verifies that filtering by rootNodeId returns only nodes grouped under those root nodes.
    ///
    /// POSITIVE PROOF: with the rootNodeId predicate block in GenerateFilter, the IN predicate
    /// is ANDed and only child nodes appear.
    ///
    /// NEGATIVE PROOF: remove the rootNodeId predicate block from GenerateFilter — all nodes
    /// are returned regardless of rootNodeId, so the ungrouped node bleeds into the result set
    /// and the <c>Does.Not.Contain(ungrouped.Id)</c> assertion fails.
    /// </summary>
    [Test]
    public async Task ListPaged_FilterByRootNodeId_ReturnsOnlyMatchingNodes()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails root = await Create(svc, name: "Root");
        NodeDetails child1 = await CreateWithRootNodeId(svc, root.Id, name: "Child1");
        NodeDetails child2 = await CreateWithRootNodeId(svc, root.Id, name: "Child2");
        NodeDetails ungrouped = await Create(svc, name: "Ungrouped");

        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPaged(
            new NodeFilter { RootNodeId = [root.Id], Count = 100 }, callerId: 0, isAdmin: true);
        List<NodeDetails> results = await CollectPage(writer);

        long[] ids = results.Select(n => n.Id).ToArray();
        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(child1.Id),      "child1 must be included (matches rootNodeId filter)");
            Assert.That(ids, Does.Contain(child2.Id),      "child2 must be included (matches rootNodeId filter)");
            Assert.That(ids, Does.Not.Contain(ungrouped.Id), "ungrouped node must be excluded");
            Assert.That(ids, Does.Not.Contain(root.Id),    "root node itself must be excluded (it has no rootNodeId set)");
        });
    }

    /// <summary>
    /// Verifies that <c>noRootNodeId=true</c> returns only nodes with no root node (ungrouped).
    ///
    /// POSITIVE PROOF: with the <c>else if (filter.NoRootNodeId)</c> branch in GenerateFilter,
    /// the <c>RootNodeId IS NULL</c> predicate is ANDed and only ungrouped nodes appear.
    ///
    /// NEGATIVE PROOF: remove the <c>else if (filter.NoRootNodeId)</c> branch — all nodes
    /// are returned regardless, so the grouped child bleeds into results and the
    /// <c>Does.Not.Contain(child.Id)</c> assertion fails.
    /// </summary>
    [Test]
    public async Task ListPaged_NoRootNodeIdFilter_ReturnsOnlyUngroupedNodes()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails root = await Create(svc, name: "Root");
        NodeDetails child = await CreateWithRootNodeId(svc, root.Id, name: "Child");
        NodeDetails ungrouped = await Create(svc, name: "Ungrouped");

        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPaged(
            new NodeFilter { NoRootNodeId = true, Count = 100 }, callerId: 0, isAdmin: true);
        List<NodeDetails> results = await CollectPage(writer);

        long[] ids = results.Select(n => n.Id).ToArray();
        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(ungrouped.Id),   "ungrouped node must be included (matches NoRootNodeId)");
            Assert.That(ids, Does.Contain(root.Id),        "root node itself must be included (it has no rootNodeId set)");
            Assert.That(ids, Does.Not.Contain(child.Id),   "grouped child must be excluded");
            Assert.That(results.All(n => n.RootNodeId == null), Is.True,
                "every returned node must have null rootNodeId");
        });
    }

    /// <summary>
    /// Verifies that when both <c>rootNodeId=&lt;list&gt;</c> and <c>noRootNodeId=true</c>
    /// are supplied the predicate uses OR semantics — matching either the list or null.
    ///
    /// POSITIVE PROOF: with the OR branch (rootNodeIdValuePredicate | null-predicate), both
    /// the grouped child and the ungrouped node are included while the other-root child is excluded.
    ///
    /// NEGATIVE PROOF: replace the OR branch with two separate AND predicates — no node satisfies
    /// both <c>RootNodeId IN (list)</c> AND <c>RootNodeId IS NULL</c> simultaneously; the result
    /// is empty and both Does.Contain assertions fail.
    /// </summary>
    [Test]
    public async Task ListPaged_RootNodeIdAndNoRootNodeId_UsesOrSemantics()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails root1 = await Create(svc, name: "Root1");
        NodeDetails root2 = await Create(svc, name: "Root2");
        NodeDetails childOfRoot1 = await CreateWithRootNodeId(svc, root1.Id, name: "ChildOfRoot1");
        NodeDetails childOfRoot2 = await CreateWithRootNodeId(svc, root2.Id, name: "ChildOfRoot2");
        NodeDetails ungrouped = await Create(svc, name: "Ungrouped");

        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPaged(
            new NodeFilter { RootNodeId = [root1.Id], NoRootNodeId = true, Count = 100 }, callerId: 0, isAdmin: true);
        List<NodeDetails> results = await CollectPage(writer);

        long[] ids = results.Select(n => n.Id).ToArray();
        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(childOfRoot1.Id),     "child of root1 must be included (matches RootNodeId list)");
            Assert.That(ids, Does.Contain(ungrouped.Id),        "ungrouped node must be included (matches NoRootNodeId)");
            Assert.That(ids, Does.Not.Contain(childOfRoot2.Id), "child of root2 must be excluded");
        });
    }

    /// <summary>
    /// Verifies that when only <c>rootNodeId=&lt;list&gt;</c> is set (no NoRootNodeId),
    /// ungrouped nodes are excluded from results.
    ///
    /// POSITIVE PROOF: the predicate is <c>RootNodeId IN (list)</c> only, so ungrouped nodes
    /// (null rootNodeId) are excluded.
    ///
    /// NEGATIVE PROOF: change the predicate to OR-include nulls — the ungrouped node appears
    /// and the Does.Not.Contain assertion fails.
    /// </summary>
    [Test]
    public async Task ListPaged_RootNodeIdOnly_ExcludesUngroupedNodes()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails root = await Create(svc, name: "Root");
        NodeDetails child = await CreateWithRootNodeId(svc, root.Id, name: "Child");
        NodeDetails ungrouped = await Create(svc, name: "Ungrouped");

        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPaged(
            new NodeFilter { RootNodeId = [root.Id], Count = 100 }, callerId: 0, isAdmin: true);
        List<NodeDetails> results = await CollectPage(writer);

        long[] ids = results.Select(n => n.Id).ToArray();
        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(child.Id),         "child node must be included");
            Assert.That(ids, Does.Not.Contain(ungrouped.Id), "ungrouped node must be excluded when only rootNodeId filter is set");
        });
    }

    /// <summary>
    /// Verifies that <c>rootNodeId</c> appears in the JSON wire shape of a list response,
    /// confirming it is included in <see cref="NodeMapper.DefaultListFields"/> and mapped.
    ///
    /// This is the §6.6 wire-shape assertion.
    ///
    /// POSITIVE PROOF: with <c>"rootNodeId"</c> in DefaultListFields and the FieldMapping
    /// registered, the serialized list response body contains the key.
    ///
    /// NEGATIVE PROOF: remove <c>"rootNodeId"</c> from DefaultListFields or drop the
    /// FieldMapping — the JSON body no longer contains the key and the Contains assertion fails.
    /// </summary>
    [Test]
    public async Task RootNodeId_AppearsInListResponseJsonWireShape()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails root = await Create(svc, name: "Root");
        await CreateWithRootNodeId(svc, root.Id, name: "Child");

        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPaged(
            new NodeFilter { Count = 10 }, callerId: 0, isAdmin: true);

        byte[] buffer;
        using (MemoryStream ms = new())
        {
            await writer.Write(ms);
            buffer = ms.ToArray();
        }

        string json = System.Text.Encoding.UTF8.GetString(buffer);

        Assert.That(json, Does.Contain("\"rootNodeId\""),
            "§6.6: list response JSON must contain the key 'rootNodeId' — " +
            "absent means rootNodeId is missing from DefaultListFields or the FieldMapping was not registered");
    }

    /// <summary>
    /// Verifies that a <c>rootNodeId</c> filter ANDs correctly with the semantic-search
    /// embedding predicate so that <c>?rootNodeId=N&amp;query=…</c> scopes the semantic
    /// result set without discarding the embedding IS NOT NULL predicate.
    ///
    /// Uses the same SQL-rendering technique as <see cref="SemanticSearchFilterCompositionTests"/>
    /// — no real Postgres connection needed; Prepare() renders SQL from the operation tree.
    ///
    /// POSITIVE PROOF: with the rootNodeId block added to GenerateFilter (before
    /// ApplySemanticSearch is called), both the rootNodeId column reference AND IS NOT NULL
    /// appear in the WHERE clause.
    ///
    /// NEGATIVE PROOF: remove the rootNodeId predicate block from GenerateFilter — the rootNodeId
    /// column reference is absent from WHERE and the Does.Contain assertion fails.
    /// </summary>
    [Test]
    public void SF_RootNodeIdFilter_WithQuery_WhereContainsBothRootNodeIdPredicateAndEmbeddingIsNotNull()
    {
        Mock<IDBClient> clientMock = new();
        clientMock.SetupGet(c => c.DBInfo).Returns(new PostgreInfo());
        IEntityManager em = new EntityManager(clientMock.Object);
        NodeService svc = new(em, new EmbeddingCapability(true));

        NodeFilter filter = new() { Query = "hivemind protocol", RootNodeId = [42L], Count = 10 };
        NodeMapper mapper = new(filter);
        filter.Fields = [.. mapper.DefaultListFields, "similarity"];

        LoadOperation<Node> operation = mapper.CreateOperation(em, filter.Fields);
        operation.ApplyFilter(filter, mapper);

        Expression<Func<Node, bool>>? generated = svc.GenerateFilter(filter, callerId: 0, isAdmin: true);
        PredicateExpression<Node>? predicate = generated != null
            ? new PredicateExpression<Node>(generated)
            : null;

        svc.ApplySemanticSearch(operation, filter, mapper, ref predicate);
        operation.Where(predicate?.Content);

        OperationPreparator preparator = new();
        ((IDatabaseOperation) operation).Prepare(preparator);
        string sql = string.Join(" ", preparator.Tokens
            .OfType<CommandTextToken>()
            .Select(t => t.Text));

        int whereIndex = sql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
        Assert.That(whereIndex, Is.GreaterThan(-1), "SQL must contain a WHERE clause");
        string whereSection = sql[whereIndex..];

        Assert.Multiple(() => {
            Assert.That(whereSection, Does.Contain("IS NOT NULL").IgnoreCase,
                "SF: WHERE clause must contain IS NOT NULL (embedding predicate) — absent means " +
                "ApplySemanticSearch was called with a second Where() that overwrote rootNodeId");
            Assert.That(whereSection, Does.Contain("rootnodeid").IgnoreCase.Or.Contain("root_node_id").IgnoreCase,
                "SF: WHERE clause must reference the rootNodeId column — absent means the rootNodeId " +
                "predicate block is missing from GenerateFilter");
        });
    }
}
