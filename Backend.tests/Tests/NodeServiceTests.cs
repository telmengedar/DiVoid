using System.Collections;
using System.Reflection;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using Pooshit.AspNetCore.Services.Errors.Exceptions;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;

namespace Backend.tests.Tests;

[TestFixture]
public class NodeServiceTests
{
    /// <summary>
    /// embedding is disabled in unit tests — SQLite does not have the embedding() function
    /// </summary>
    static readonly IEmbeddingCapability DisabledCapability = new EmbeddingCapability(false);

    static NodeService MakeService(DatabaseFixture fixture) => new(fixture.EntityManager, DisabledCapability);

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    static Task<NodeDetails> Create(NodeService svc, string type = "task", string name = "Test node")
        => svc.CreateNode(new NodeDetails { Type = type, Name = name }, callerId: 0);

    static async Task<NodeDetails> CreateWithStatus(NodeService svc, string status, string type = "task", string name = "Test node")
    {
        NodeDetails node = await svc.CreateNode(new NodeDetails { Type = type, Name = name }, callerId: 0);
        return await svc.Patch(node.Id, [new PatchOperation { Op = "replace", Path = "/status", Value = status }], callerId: 0, isAdmin: true, CancellationToken.None);
    }

    // -----------------------------------------------------------------------
    // CreateNode
    // -----------------------------------------------------------------------

    [Test]
    public async Task CreateNode_AssignsId()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails result = await Create(svc);

        Assert.That(result.Id, Is.GreaterThan(0));
    }

    [Test]
    public async Task CreateNode_NewType_CreatesNodeType()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        await Create(svc, type: "uniquetype1");

        long count = await fixture.EntityManager.Load<NodeType>(Pooshit.Ocelot.Tokens.DB.Count())
                                  .Where(t => t.Type == "uniquetype1")
                                  .ExecuteScalarAsync<long>();
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public async Task CreateNode_ExistingType_ReusesNodeType()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        await Create(svc, type: "sharedtype");
        await Create(svc, type: "sharedtype");

        long count = await fixture.EntityManager.Load<NodeType>(Pooshit.Ocelot.Tokens.DB.Count())
                                  .Where(t => t.Type == "sharedtype")
                                  .ExecuteScalarAsync<long>();
        Assert.That(count, Is.EqualTo(1));
    }

    // -----------------------------------------------------------------------
    // Delete
    // -----------------------------------------------------------------------

    [Test]
    public async Task Delete_ExistingNode_RemovesIt()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await Create(svc);
        await svc.Delete(node.Id, callerId: 0, isAdmin: true);

        Assert.ThrowsAsync<NotFoundException<Node>>(() => svc.GetNodeById(node.Id, callerId: 0, isAdmin: true));
    }

    [Test]
    public void Delete_MissingNode_ThrowsNotFoundException()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        Assert.ThrowsAsync<NotFoundException<Node>>(() => svc.Delete(99999, callerId: 0, isAdmin: true));
    }

    [Test]
    public async Task Delete_RemovesAssociatedLinks()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails a = await Create(svc, name: "A");
        NodeDetails b = await Create(svc, name: "B");
        await svc.LinkNodes(a.Id, b.Id, callerId: 0, isAdmin: true);

        await svc.Delete(a.Id, callerId: 0, isAdmin: true);

        long linkCount = await fixture.EntityManager.Load<NodeLink>(Pooshit.Ocelot.Tokens.DB.Count())
                                      .Where(l => l.SourceId == a.Id || l.TargetId == a.Id)
                                      .ExecuteScalarAsync<long>();
        Assert.That(linkCount, Is.EqualTo(0), "Link rows referencing the deleted node must be removed by Delete()");
    }

    [Test]
    public async Task Delete_RemovesAssociatedLinks_TargetSide()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails a = await Create(svc, name: "A");
        NodeDetails b = await Create(svc, name: "B");
        await svc.LinkNodes(a.Id, b.Id, callerId: 0, isAdmin: true); // stored as SourceId=a, TargetId=b

        await svc.Delete(b.Id, callerId: 0, isAdmin: true); // exercises the TargetId == nodeId branch of the OR predicate

        long linkCount = await fixture.EntityManager.Load<NodeLink>(Pooshit.Ocelot.Tokens.DB.Count())
                                      .Where(l => l.SourceId == b.Id || l.TargetId == b.Id)
                                      .ExecuteScalarAsync<long>();
        Assert.That(linkCount, Is.EqualTo(0), "Link rows referencing the deleted node must be removed by Delete() (target-side branch)");
    }

    // -----------------------------------------------------------------------
    // GetNodeData
    // -----------------------------------------------------------------------

    [Test]
    public async Task GetNodeData_ExistingNode_ReturnsContentTypeAndBytes()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await Create(svc);
        byte[] content = "hello world"u8.ToArray();
        await svc.UploadContent(node.Id, "text/plain", new MemoryStream(content), callerId: 0, isAdmin: true);

        (string contentType, Stream stream) = await svc.GetNodeData(node.Id, callerId: 0, isAdmin: true);
        byte[] bytes = new byte[content.Length];
        await stream.ReadExactlyAsync(bytes);

        Assert.Multiple(() => {
            Assert.That(contentType, Is.EqualTo("text/plain"));
            Assert.That(bytes, Is.EqualTo(content));
        });
    }

    [Test]
    public void GetNodeData_MissingNode_ThrowsNotFoundException()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        Assert.ThrowsAsync<NotFoundException<Node>>(() => svc.GetNodeData(99999, callerId: 0, isAdmin: true));
    }

    // -----------------------------------------------------------------------
    // LinkNodes
    // -----------------------------------------------------------------------

    [Test]
    public async Task LinkNodes_ValidPair_InsertsLink()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails a = await Create(svc, name: "A");
        NodeDetails b = await Create(svc, name: "B");
        await svc.LinkNodes(a.Id, b.Id, callerId: 0, isAdmin: true);

        long count = await fixture.EntityManager.Load<NodeLink>(Pooshit.Ocelot.Tokens.DB.Count())
                                  .Where(l => l.SourceId == a.Id && l.TargetId == b.Id)
                                  .ExecuteScalarAsync<long>();
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public void LinkNodes_SelfLink_ThrowsInvalidOperationException()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        Assert.ThrowsAsync<InvalidOperationException>(() => svc.LinkNodes(1, 1, callerId: 0, isAdmin: true));
    }

    [Test]
    public async Task LinkNodes_MissingSourceNode_ThrowsNotFoundException()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails b = await Create(svc, name: "B");

        Assert.ThrowsAsync<NotFoundException<Node>>(() => svc.LinkNodes(99999, b.Id, callerId: 0, isAdmin: true));
    }

    [Test]
    public async Task LinkNodes_MissingTargetNode_ThrowsNotFoundException()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails a = await Create(svc, name: "A");

        Assert.ThrowsAsync<NotFoundException<Node>>(() => svc.LinkNodes(a.Id, 99999, callerId: 0, isAdmin: true));
    }

    [Test]
    public async Task LinkNodes_DuplicateLink_IsIdempotent()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails a = await Create(svc, name: "A");
        NodeDetails b = await Create(svc, name: "B");
        await svc.LinkNodes(a.Id, b.Id, callerId: 0, isAdmin: true);

        // Second call in same direction must succeed silently (bug #702 regression)
        Assert.DoesNotThrowAsync(() => svc.LinkNodes(a.Id, b.Id, callerId: 0, isAdmin: true));

        // Exactly one link row must exist — not zero, not two
        long count = await fixture.EntityManager.Load<NodeLink>(Pooshit.Ocelot.Tokens.DB.Count())
                                  .Where(l => l.SourceId == a.Id && l.TargetId == b.Id)
                                  .ExecuteScalarAsync<long>();
        Assert.That(count, Is.EqualTo(1), "duplicate POST must not insert a second link row");
    }

    [Test]
    public async Task LinkNodes_DuplicateLinkReverseDirection_IsIdempotent()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails a = await Create(svc, name: "A");
        NodeDetails b = await Create(svc, name: "B");
        await svc.LinkNodes(a.Id, b.Id, callerId: 0, isAdmin: true);

        // Reverse direction is still a duplicate on an undirected graph — must succeed silently
        Assert.DoesNotThrowAsync(() => svc.LinkNodes(b.Id, a.Id, callerId: 0, isAdmin: true));

        long count = await fixture.EntityManager.Load<NodeLink>(Pooshit.Ocelot.Tokens.DB.Count())
                                  .Where(l => (l.SourceId == a.Id && l.TargetId == b.Id) || (l.SourceId == b.Id && l.TargetId == a.Id))
                                  .ExecuteScalarAsync<long>();
        Assert.That(count, Is.EqualTo(1), "reverse-direction duplicate POST must not insert a second link row");
    }

    // -----------------------------------------------------------------------
    // UnlinkNodes
    // -----------------------------------------------------------------------

    [Test]
    public async Task UnlinkNodes_ExistingLink_RemovesLink()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails a = await Create(svc, name: "A");
        NodeDetails b = await Create(svc, name: "B");
        await svc.LinkNodes(a.Id, b.Id, callerId: 0, isAdmin: true);

        await svc.UnlinkNodes(a.Id, b.Id, callerId: 0, isAdmin: true);

        long count = await fixture.EntityManager.Load<NodeLink>(Pooshit.Ocelot.Tokens.DB.Count())
                                  .Where(l => l.SourceId == a.Id && l.TargetId == b.Id)
                                  .ExecuteScalarAsync<long>();
        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public async Task UnlinkNodes_ReverseDirection_RemovesLink()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails a = await Create(svc, name: "A");
        NodeDetails b = await Create(svc, name: "B");
        await svc.LinkNodes(a.Id, b.Id, callerId: 0, isAdmin: true); // stored as (a, b)

        // Unlink from the b→a direction should still work
        await svc.UnlinkNodes(b.Id, a.Id, callerId: 0, isAdmin: true);

        long count = await fixture.EntityManager.Load<NodeLink>(Pooshit.Ocelot.Tokens.DB.Count())
                                  .Where(l => l.SourceId == a.Id && l.TargetId == b.Id)
                                  .ExecuteScalarAsync<long>();
        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public async Task UnlinkNodes_NoLink_IsIdempotent()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails a = await Create(svc, name: "A");
        NodeDetails b = await Create(svc, name: "B");

        // DELETE of a non-existent link must succeed silently (bug #702 sibling check)
        Assert.DoesNotThrowAsync(() => svc.UnlinkNodes(a.Id, b.Id, callerId: 0, isAdmin: true));
    }

    // -----------------------------------------------------------------------
    // ListPaged — filtering
    // -----------------------------------------------------------------------

    [Test]
    public async Task ListPaged_NoFilter_ReturnsAllNodes()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        await Create(svc, name: "N1");
        await Create(svc, name: "N2");
        await Create(svc, name: "N3");

        long count = await GetPageCount(svc, new NodeFilter { Count = 100 });
        Assert.That(count, Is.GreaterThanOrEqualTo(3));
    }

    [Test]
    public async Task ListPaged_FilterById_ReturnsSingleNode()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails n = await Create(svc, name: "Unique");
        await Create(svc, name: "Other");

        long count = await GetPageCount(svc, new NodeFilter { Id = [n.Id], Count = 100 });
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public async Task ListPaged_FilterByType_ReturnsOnlyMatchingType()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        await Create(svc, type: "alpha", name: "A");
        await Create(svc, type: "beta", name: "B");
        await Create(svc, type: "alpha", name: "C");

        long count = await GetPageCount(svc, new NodeFilter { Type = ["alpha"], Count = 100 });
        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task ListPaged_FilterByName_ExactMatch()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        await Create(svc, name: "Target");
        await Create(svc, name: "NotTarget");

        long count = await GetPageCount(svc, new NodeFilter { Name = ["Target"], Count = 100 });
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public async Task ListPaged_FilterByName_WildcardPercent_UsesLike()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        await Create(svc, name: "FooBar");
        await Create(svc, name: "FooBaz");
        await Create(svc, name: "XYZ");

        // "Foo%" should match FooBar and FooBaz
        long count = await GetPageCount(svc, new NodeFilter { Name = ["Foo%"], Count = 100 });
        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task ListPaged_FilterByName_WildcardUnderscore_UsesLike()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        await Create(svc, name: "FooA");
        await Create(svc, name: "FooB");
        await Create(svc, name: "Bar");

        // "Foo_" should match FooA and FooB
        long count = await GetPageCount(svc, new NodeFilter { Name = ["Foo_"], Count = 100 });
        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task ListPaged_FilterByLinkedTo_ReturnsBothDirectionNeighbours()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails hub = await Create(svc, name: "Hub");
        NodeDetails left = await Create(svc, name: "Left");
        NodeDetails right = await Create(svc, name: "Right");
        NodeDetails unrelated = await Create(svc, name: "Unrelated");

        // left → hub, hub → right
        await svc.LinkNodes(left.Id, hub.Id, callerId: 0, isAdmin: true);
        await svc.LinkNodes(hub.Id, right.Id, callerId: 0, isAdmin: true);

        // Query for nodes linked to hub — should return left and right, not hub itself
        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPaged(new NodeFilter { LinkedTo = [hub.Id], Count = 100 }, callerId: 0, isAdmin: true);
        List<NodeDetails> results = await CollectPage(writer);

        long[] ids = results.Select(n => n.Id).ToArray();
        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(left.Id));
            Assert.That(ids, Does.Contain(right.Id));
            Assert.That(ids, Does.Not.Contain(hub.Id));
            Assert.That(ids, Does.Not.Contain(unrelated.Id));
        });
    }

    [Test]
    public async Task ListPaged_Paging_RespectsCountAndContinue()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        for (int i = 0; i < 5; i++)
            await Create(svc, name: $"Page{i}");

        AsyncPageResponseWriter<NodeDetails> page1Writer = await svc.ListPaged(new NodeFilter { Count = 3 }, callerId: 0, isAdmin: true);
        List<NodeDetails> page1 = await CollectPage(page1Writer);

        AsyncPageResponseWriter<NodeDetails> page2Writer = await svc.ListPaged(new NodeFilter { Count = 3, Continue = 3 }, callerId: 0, isAdmin: true);
        List<NodeDetails> page2 = await CollectPage(page2Writer);

        Assert.Multiple(() => {
            Assert.That(page1.Count, Is.EqualTo(3));
            // page 2 may have fewer items but no overlap with page 1
            Assert.That(page2.Select(n => n.Id).Intersect(page1.Select(n => n.Id)), Is.Empty);
        });
    }

    [Test]
    public async Task ListPaged_SortByName_Descending_OrdersCorrectly()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        await Create(svc, name: "Apple");
        await Create(svc, name: "Zebra");
        await Create(svc, name: "Mango");

        // Sort key must be one of NodeMapper's registered keys ("id", "type", "name", "status").
        // The test uses "name" to exercise the ascending/descending path.
        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPaged(new NodeFilter
        {
            Count = 100,
            Sort = "name",
            Descending = true
        }, callerId: 0, isAdmin: true);
        List<NodeDetails> results = await CollectPage(writer);

        List<string> names = results.Select(n => n.Name!).ToList();
        // Filter to our three nodes in case the DB has pre-existing data
        List<string> ours = names.Where(n => n is "Apple" or "Zebra" or "Mango").ToList();
        Assert.That(ours, Is.EqualTo(ours.OrderByDescending(n => n).ToList()));
    }

    [Test]
    public async Task ListPaged_SortByNodeName_TwoPart_ThrowsKeyNotFound()
    {
        // NodeService.ListPaged routes sorting through the mapper-based ApplyFilter overload,
        // which does a strict dictionary lookup. NodeMapper registers "id", "type", "name",
        // "status" — two-part keys like "node.name" are not registered and throw KeyNotFoundException.
        // This is intentional: callers sort by the fields the mapper exposes, not by join aliases.
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);
        await Create(svc, name: "A");

        Assert.ThrowsAsync<Pooshit.Ocelot.Errors.UnknownFieldException>(
            () => svc.ListPaged(new NodeFilter { Count = 10, Sort = "node.name" }, callerId: 0, isAdmin: true));
    }

    // -----------------------------------------------------------------------
    // ListPaged — CancellationToken signature regression (DiVoid #872)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Load-bearing signature regression test (DiVoid #275).
    ///
    /// Asserts that <see cref="INodeService.ListPaged"/> carries a <c>CancellationToken</c>
    /// parameter so streaming reads on the non-path branch are cancelled on client disconnect.
    ///
    /// NEGATIVE PROOF: remove the <c>CancellationToken</c> parameter from
    /// <see cref="INodeService.ListPaged"/> — this test fails with the message below.
    /// POSITIVE PROOF: with the parameter present this test passes.
    /// </summary>
    [Test]
    public void ListPaged_SignatureIncludesCancellationToken()
    {
        MethodInfo? method = typeof(INodeService)
            .GetMethod(nameof(INodeService.ListPaged));

        Assert.That(method, Is.Not.Null,
            "T15: INodeService must expose a ListPaged method");

        ParameterInfo[] parameters = method!.GetParameters();
        bool hasCancellationToken = parameters.Any(p => p.ParameterType == typeof(CancellationToken));

        Assert.That(hasCancellationToken, Is.True,
            "T15 (CRITICAL): INodeService.ListPaged must accept a CancellationToken parameter " +
            "so that streaming reads are cancelled on client disconnect (DiVoid #872). " +
            "A failure here means the CT plumbing has been removed and cursor leaks on Postgres/SQLite " +
            "are possible again.");
    }

    // -----------------------------------------------------------------------
    // ListTypes — CancellationToken signature regression (DiVoid #875)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Load-bearing signature regression test (DiVoid #275).
    ///
    /// Asserts that <see cref="INodeService.ListTypes"/> carries a <c>CancellationToken</c>
    /// parameter so streaming reads on the types endpoint are cancelled on client disconnect.
    ///
    /// NEGATIVE PROOF: remove the <c>CancellationToken</c> parameter from
    /// <see cref="INodeService.ListTypes"/> — this test fails with the message below.
    /// POSITIVE PROOF: with the parameter present this test passes.
    /// </summary>
    [Test]
    public void ListTypes_SignatureIncludesCancellationToken()
    {
        MethodInfo? method = typeof(INodeService)
            .GetMethod(nameof(INodeService.ListTypes));

        Assert.That(method, Is.Not.Null,
            "T15: INodeService must expose a ListTypes method");

        ParameterInfo[] parameters = method!.GetParameters();
        bool hasCancellationToken = parameters.Any(p => p.ParameterType == typeof(CancellationToken));

        Assert.That(hasCancellationToken, Is.True,
            "T15 (CRITICAL): INodeService.ListTypes must accept a CancellationToken parameter " +
            "so that streaming reads are cancelled on client disconnect (DiVoid #875). " +
            "A failure here means the CT plumbing has been removed and cursor leaks on Postgres/SQLite " +
            "are possible again.");
    }

    // -----------------------------------------------------------------------
    // Patch — Node has no [AllowPatch] properties at time of writing.
    // -----------------------------------------------------------------------

    [Test]
    public void Patch_UnknownNodeId_ThrowsNotFoundException()
    {
        // Path "/nonexistent" causes PropertyNotFoundException inside Patch extension,
        // which bubbles out before touching the DB. With Status now [AllowPatch] we can
        // test a valid path against a missing row id.
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        Assert.ThrowsAsync<NotFoundException<Node>>(
            () => svc.Patch(99999, [new PatchOperation { Op = "replace", Path = "/status", Value = "open" }], callerId: 0, isAdmin: true, CancellationToken.None));
    }

    // -----------------------------------------------------------------------
    // Patch — Status field (replace op happy path)
    // -----------------------------------------------------------------------

    [Test]
    public async Task Patch_ReplaceStatus_UpdatesField()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await Create(svc);
        NodeDetails result = await svc.Patch(node.Id, [new PatchOperation { Op = "replace", Path = "/status", Value = "open" }], callerId: 0, isAdmin: true, CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo("open"));
    }

    [Test]
    public async Task Patch_ReplaceStatus_OverwritesExistingStatus()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await Create(svc);
        await svc.Patch(node.Id, [new PatchOperation { Op = "replace", Path = "/status", Value = "open" }], callerId: 0, isAdmin: true, CancellationToken.None);
        NodeDetails result = await svc.Patch(node.Id, [new PatchOperation { Op = "replace", Path = "/status", Value = "closed" }], callerId: 0, isAdmin: true, CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo("closed"));
    }

    // -----------------------------------------------------------------------
    // Patch — Name field
    // -----------------------------------------------------------------------

    [Test]
    public async Task Patch_ReplaceName_UpdatesField()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await Create(svc, name: "OriginalName");
        NodeDetails result = await svc.Patch(node.Id, [new PatchOperation { Op = "replace", Path = "/name", Value = "UpdatedName" }], callerId: 0, isAdmin: true, CancellationToken.None);

        Assert.That(result.Name, Is.EqualTo("UpdatedName"));
    }

    [Test]
    public async Task Patch_ReplaceName_PersistedToDatabase()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await Create(svc, name: "BeforePatch");
        await svc.Patch(node.Id, [new PatchOperation { Op = "replace", Path = "/name", Value = "AfterPatch" }], callerId: 0, isAdmin: true, CancellationToken.None);

        // Reload via list to confirm DB was written
        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPaged(new NodeFilter { Id = [node.Id], Count = 1 }, callerId: 0, isAdmin: true);
        List<NodeDetails> results = await CollectPage(writer);
        Assert.That(results.Single().Name, Is.EqualTo("AfterPatch"));
    }

    [Test]
    public async Task Patch_ReplaceType_ThrowsPropertyNotFoundException()
    {
        // The PATCH path "/type" does not map to any property on Node — the DB entity
        // stores type as TypeId (long), not as "type". The extension throws
        // PropertyNotFoundException before the [AllowPatch] check is reached.
        // Either way, /type is not patchable and the middleware returns 400.
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await Create(svc);

        Assert.ThrowsAsync<PropertyNotFoundException>(
            () => svc.Patch(node.Id, [new PatchOperation { Op = "replace", Path = "/type", Value = "other" }], callerId: 0, isAdmin: true, CancellationToken.None));
    }

    [Test]
    public async Task Patch_ReplaceStatus_StillWorksAfterNamePatchAdded()
    {
        // Regression: adding [AllowPatch] to Name must not break the existing Status patch path.
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await Create(svc);
        NodeDetails result = await svc.Patch(node.Id, [new PatchOperation { Op = "replace", Path = "/status", Value = "in-progress" }], callerId: 0, isAdmin: true, CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo("in-progress"));
    }

    // TODO: Patch_AddStatus_IsNotMeaningfulForString — the patch infrastructure does not
    // validate ops against property type, so "add" on a string field currently behaves
    // as a replace rather than throwing. Fixing this is out of scope (affects all string
    // fields); track as a separate concern per task 26.

    // TODO: Patch_RemoveStatus_IsNotMeaningfulForString — same infrastructure gap as above.

    // TODO: Patch_FlagStatus_IsNotMeaningfulForString — same.

    // -----------------------------------------------------------------------
    // ListPaged — status filter
    // -----------------------------------------------------------------------

    [Test]
    public async Task ListPaged_FilterByStatus_SingleValue_ReturnsMatchingNodes()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        await CreateWithStatus(svc, "open", name: "Open1");
        await CreateWithStatus(svc, "open", name: "Open2");
        await CreateWithStatus(svc, "closed", name: "Closed1");
        await Create(svc, name: "NoStatus");

        long count = await GetPageCount(svc, new NodeFilter { Status = ["open"], Count = 100 });
        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task ListPaged_FilterByStatus_MultiValue_InStyle()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        await CreateWithStatus(svc, "open", name: "Open1");
        await CreateWithStatus(svc, "in-progress", name: "InProgress1");
        await CreateWithStatus(svc, "closed", name: "Closed1");
        await Create(svc, name: "NoStatus");

        long count = await GetPageCount(svc, new NodeFilter { Status = ["open", "in-progress"], Count = 100 });
        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task ListPaged_FilterByStatus_Wildcard_UsesLike()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        await CreateWithStatus(svc, "open", name: "Open1");
        await CreateWithStatus(svc, "open-review", name: "OpenReview");
        await CreateWithStatus(svc, "closed", name: "Closed1");

        // "open%" should match "open" and "open-review"
        long count = await GetPageCount(svc, new NodeFilter { Status = ["open%"], Count = 100 });
        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task ListPaged_FilterByStatus_ComposesWithType()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        await CreateWithStatus(svc, "open", type: "task", name: "TaskOpen");
        await CreateWithStatus(svc, "open", type: "bug", name: "BugOpen");
        await CreateWithStatus(svc, "closed", type: "task", name: "TaskClosed");

        long count = await GetPageCount(svc, new NodeFilter { Type = ["task"], Status = ["open"], Count = 100 });
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public async Task ListPaged_FilterByStatus_ComposesWithLinkedTo()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails project = await Create(svc, type: "project", name: "Project");
        NodeDetails openTask = await CreateWithStatus(svc, "open", type: "task", name: "OpenTask");
        NodeDetails closedTask = await CreateWithStatus(svc, "closed", type: "task", name: "ClosedTask");
        NodeDetails unlinked = await CreateWithStatus(svc, "open", type: "task", name: "UnlinkedOpen");

        await svc.LinkNodes(project.Id, openTask.Id, callerId: 0, isAdmin: true);
        await svc.LinkNodes(project.Id, closedTask.Id, callerId: 0, isAdmin: true);

        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPaged(new NodeFilter { LinkedTo = [project.Id], Status = ["open"], Count = 100 }, callerId: 0, isAdmin: true);
        List<NodeDetails> results = await CollectPage(writer);

        long[] ids = results.Select(n => n.Id).ToArray();
        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(openTask.Id));
            Assert.That(ids, Does.Not.Contain(closedTask.Id));
            Assert.That(ids, Does.Not.Contain(unlinked.Id));
            Assert.That(ids, Does.Not.Contain(project.Id));
        });
    }

    [Test]
    public async Task ListPaged_NoStatusFilter_ReturnsOnlyNoStatusNodes()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails noStatus = await Create(svc, name: "NoStatus");
        await CreateWithStatus(svc, "open", name: "HasStatus");

        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPaged(new NodeFilter { NoStatus = true, Count = 100 }, callerId: 0, isAdmin: true);
        List<NodeDetails> results = await CollectPage(writer);

        long[] ids = results.Select(n => n.Id).ToArray();
        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(noStatus.Id));
            // nodes with a non-empty status must not appear
            Assert.That(results.All(n => string.IsNullOrEmpty(n.Status)), Is.True);
        });
    }

    [Test]
    public async Task ListPaged_NoStatusFilter_ExcludesNodesWithStatus()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails withStatus = await CreateWithStatus(svc, "open", name: "WithStatus");

        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPaged(new NodeFilter { NoStatus = true, Count = 100 }, callerId: 0, isAdmin: true);
        List<NodeDetails> results = await CollectPage(writer);

        Assert.That(results.Select(n => n.Id), Does.Not.Contain(withStatus.Id));
    }

    // -----------------------------------------------------------------------
    // ListPaged — nostatus=true + status=<list> OR semantics (bug #321)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Load-bearing test for bug #321.
    ///
    /// When both <c>Status</c> and <c>NoStatus=true</c> are supplied, the predicate must
    /// be <c>(Status IN (list) OR Status IS NULL)</c>, not the impossible
    /// <c>(Status IN (list)) AND (Status IS NULL)</c>.
    ///
    /// POSITIVE PROOF: with the OR fix this test passes — the result set includes both
    /// the "open" node and the null-status node, while "closed" is excluded.
    ///
    /// NEGATIVE PROOF: revert the fix to two separate <c>predicate &amp;=</c> calls (AND semantics).
    /// The result set becomes empty because no row satisfies both branches simultaneously,
    /// so the Assert.Multiple fails on both expected ids.
    /// </summary>
    [Test]
    public async Task ListPaged_StatusAndNoStatus_UsesOrSemantics()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails openNode   = await CreateWithStatus(svc, "open",   name: "OpenNode");
        NodeDetails closedNode = await CreateWithStatus(svc, "closed", name: "ClosedNode");
        NodeDetails nullNode   = await Create(svc, name: "NullStatusNode");

        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPaged(new NodeFilter { Status = ["open", "in-progress"], NoStatus = true, Count = 100 }, callerId: 0, isAdmin: true);
        List<NodeDetails> results = await CollectPage(writer);

        long[] ids = results.Select(n => n.Id).ToArray();
        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(openNode.Id),   "open-status node must be included (matches Status list)");
            Assert.That(ids, Does.Contain(nullNode.Id),   "null-status node must be included (matches NoStatus)");
            Assert.That(ids, Does.Not.Contain(closedNode.Id), "closed-status node must be excluded");
        });
    }

    /// <summary>
    /// Regression: when only <c>Status=["open"]</c> is supplied (no NoStatus), only the open
    /// node is returned — null-status nodes are excluded.
    /// </summary>
    [Test]
    public async Task ListPaged_StatusOnly_ExcludesNullStatusNodes()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails openNode   = await CreateWithStatus(svc, "open", name: "OpenNode");
        NodeDetails nullNode   = await Create(svc, name: "NullStatusNode");

        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPaged(new NodeFilter { Status = ["open"], Count = 100 }, callerId: 0, isAdmin: true);
        List<NodeDetails> results = await CollectPage(writer);

        long[] ids = results.Select(n => n.Id).ToArray();
        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(openNode.Id),       "open-status node must be included");
            Assert.That(ids, Does.Not.Contain(nullNode.Id),   "null-status node must be excluded when only status filter is set");
        });
    }

    /// <summary>
    /// Regression: when only <c>NoStatus=true</c> is supplied (no Status list), only
    /// null-status nodes are returned — nodes with any status are excluded.
    /// </summary>
    [Test]
    public async Task ListPaged_NoStatusOnly_ExcludesNodesWithStatus()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails openNode   = await CreateWithStatus(svc, "open", name: "OpenNode");
        NodeDetails nullNode   = await Create(svc, name: "NullStatusNode");

        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPaged(new NodeFilter { NoStatus = true, Count = 100 }, callerId: 0, isAdmin: true);
        List<NodeDetails> results = await CollectPage(writer);

        long[] ids = results.Select(n => n.Id).ToArray();
        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(nullNode.Id),       "null-status node must be included");
            Assert.That(ids, Does.Not.Contain(openNode.Id),   "open-status node must be excluded when only nostatus filter is set");
        });
    }

    [Test]
    public async Task ListPaged_StatusAppearsInDefaultFields()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await Create(svc, name: "CheckFields");
        await svc.Patch(node.Id, [new PatchOperation { Op = "replace", Path = "/status", Value = "open" }], callerId: 0, isAdmin: true, CancellationToken.None);

        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPaged(new NodeFilter { Id = [node.Id], Count = 100 }, callerId: 0, isAdmin: true);
        List<NodeDetails> results = await CollectPage(writer);

        Assert.That(results.Single().Status, Is.EqualTo("open"));
    }

    // -----------------------------------------------------------------------
    // ListPaged — severity filter
    // -----------------------------------------------------------------------

    static async Task<NodeDetails> CreateWithSeverity(NodeService svc, int severity, string type = "task", string name = "Test node")
    {
        NodeDetails node = await svc.CreateNode(new NodeDetails { Type = type, Name = name }, callerId: 0);
        return await svc.Patch(node.Id, [new PatchOperation { Op = "replace", Path = "/severity", Value = severity }], callerId: 0, isAdmin: true, CancellationToken.None);
    }

    [Test]
    public async Task ListPaged_FilterBySeverity_SingleValue_ReturnsMatchingNodes()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails sev3 = await CreateWithSeverity(svc, 3, name: "Sev3");
        NodeDetails sev5 = await CreateWithSeverity(svc, 5, name: "Sev5");
        await Create(svc, name: "NoSeverity");

        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPaged(new NodeFilter { Severity = [3], Count = 100 }, callerId: 0, isAdmin: true);
        List<NodeDetails> results = await CollectPage(writer);

        long[] ids = results.Select(n => n.Id).ToArray();
        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(sev3.Id));
            Assert.That(ids, Does.Not.Contain(sev5.Id));
        });
    }

    [Test]
    public async Task ListPaged_FilterBySeverity_MultiValue_InStyle()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails sev1 = await CreateWithSeverity(svc, 1, name: "Sev1");
        NodeDetails sev3 = await CreateWithSeverity(svc, 3, name: "Sev3");
        NodeDetails sev5 = await CreateWithSeverity(svc, 5, name: "Sev5");

        long count = await GetPageCount(svc, new NodeFilter { Severity = [1, 3], Count = 100 });
        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task ListPaged_FilterBySeverityRange_ReturnsNodesInRange()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails sev1 = await CreateWithSeverity(svc, 1, name: "Sev1");
        NodeDetails sev3 = await CreateWithSeverity(svc, 3, name: "Sev3");
        NodeDetails sev5 = await CreateWithSeverity(svc, 5, name: "Sev5");
        NodeDetails sev7 = await CreateWithSeverity(svc, 7, name: "Sev7");

        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPaged(new NodeFilter { SeverityMin = 3, SeverityMax = 5, Count = 100 }, callerId: 0, isAdmin: true);
        List<NodeDetails> results = await CollectPage(writer);

        long[] ids = results.Select(n => n.Id).ToArray();
        Assert.Multiple(() => {
            Assert.That(ids, Does.Not.Contain(sev1.Id));
            Assert.That(ids, Does.Contain(sev3.Id));
            Assert.That(ids, Does.Contain(sev5.Id));
            Assert.That(ids, Does.Not.Contain(sev7.Id));
        });
    }

    [Test]
    public async Task ListPaged_NoSeverityFilter_ReturnsOnlyNoSeverityNodes()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails noSeverity = await Create(svc, name: "NoSeverity");
        NodeDetails hasSeverity = await CreateWithSeverity(svc, 5, name: "HasSeverity");

        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPaged(new NodeFilter { NoSeverity = true, Count = 100 }, callerId: 0, isAdmin: true);
        List<NodeDetails> results = await CollectPage(writer);

        long[] ids = results.Select(n => n.Id).ToArray();
        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(noSeverity.Id));
            Assert.That(ids, Does.Not.Contain(hasSeverity.Id));
        });
    }

    [Test]
    public async Task ListPaged_SeverityAndNoSeverity_UsesOrSemantics()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails sev3 = await CreateWithSeverity(svc, 3, name: "Sev3");
        NodeDetails sev5 = await CreateWithSeverity(svc, 5, name: "Sev5");
        NodeDetails noSeverity = await Create(svc, name: "NoSeverity");

        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPaged(new NodeFilter { Severity = [3], NoSeverity = true, Count = 100 }, callerId: 0, isAdmin: true);
        List<NodeDetails> results = await CollectPage(writer);

        long[] ids = results.Select(n => n.Id).ToArray();
        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(sev3.Id),      "severity=3 node must be included (matches Severity list)");
            Assert.That(ids, Does.Contain(noSeverity.Id), "null-severity node must be included (matches NoSeverity)");
            Assert.That(ids, Does.Not.Contain(sev5.Id),   "severity=5 node must be excluded");
        });
    }

    [Test]
    public async Task ListPaged_FilterBySeverity_ComposesWithType()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails taskSev3 = await CreateWithSeverity(svc, 3, type: "task", name: "TaskSev3");
        NodeDetails bugSev3 = await CreateWithSeverity(svc, 3, type: "bug", name: "BugSev3");
        NodeDetails taskSev5 = await CreateWithSeverity(svc, 5, type: "task", name: "TaskSev5");

        long count = await GetPageCount(svc, new NodeFilter { Type = ["task"], Severity = [3], Count = 100 });
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public async Task ListPaged_SortBySeverity_Ascending_OrdersCorrectly()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails sev5 = await CreateWithSeverity(svc, 5, name: "Sev5");
        NodeDetails sev1 = await CreateWithSeverity(svc, 1, name: "Sev1");
        NodeDetails sev3 = await CreateWithSeverity(svc, 3, name: "Sev3");

        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPaged(new NodeFilter
        {
            Id = [sev1.Id, sev3.Id, sev5.Id],
            Count = 100,
            Sort = "severity",
            Descending = false
        }, callerId: 0, isAdmin: true);
        List<NodeDetails> results = await CollectPage(writer);

        List<int?> severities = results.Select(n => n.Severity).ToList();
        Assert.That(severities, Is.EqualTo(severities.OrderBy(s => s).ToList()));
    }

    [Test]
    public async Task ListPaged_SortBySeverity_Descending_OrdersCorrectly()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails sev5 = await CreateWithSeverity(svc, 5, name: "SortSev5");
        NodeDetails sev1 = await CreateWithSeverity(svc, 1, name: "SortSev1");
        NodeDetails sev3 = await CreateWithSeverity(svc, 3, name: "SortSev3");

        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPaged(new NodeFilter
        {
            Id = [sev1.Id, sev3.Id, sev5.Id],
            Count = 100,
            Sort = "severity",
            Descending = true
        }, callerId: 0, isAdmin: true);
        List<NodeDetails> results = await CollectPage(writer);

        List<int?> severities = results.Select(n => n.Severity).ToList();
        Assert.That(severities, Is.EqualTo(severities.OrderByDescending(s => s).ToList()));
    }

    [Test]
    public async Task PathQuery_SeverityHop_ReturnsOnlyMatchingSeverity()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails hub = await Create(svc, type: "project", name: "Hub");
        NodeDetails sev5 = await CreateWithSeverity(svc, 5, type: "task", name: "HopSev5");
        NodeDetails sev3 = await CreateWithSeverity(svc, 3, type: "task", name: "HopSev3");
        await svc.LinkNodes(hub.Id, sev5.Id, callerId: 0, isAdmin: true);
        await svc.LinkNodes(hub.Id, sev3.Id, callerId: 0, isAdmin: true);

        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPagedByPath(
            new NodePathFilter { Path = $"[type:project,name:Hub]/[type:task,severity:5]", Count = 100 },
            callerId: 0, isAdmin: true, CancellationToken.None);
        List<NodeDetails> results = await CollectPage(writer);

        long[] ids = results.Select(n => n.Id).ToArray();
        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(sev5.Id));
            Assert.That(ids, Does.Not.Contain(sev3.Id));
        });
    }

    // -----------------------------------------------------------------------
    // UploadContent
    // -----------------------------------------------------------------------

    [Test]
    public async Task UploadContent_ExistingNode_StoresBytes()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await Create(svc);
        byte[] data = "test content"u8.ToArray();

        await svc.UploadContent(node.Id, "text/markdown", new MemoryStream(data), callerId: 0, isAdmin: true);

        (string ct, Stream stream) = await svc.GetNodeData(node.Id, callerId: 0, isAdmin: true);
        byte[] stored = new byte[data.Length];
        await stream.ReadExactlyAsync(stored);

        Assert.Multiple(() => {
            Assert.That(ct, Is.EqualTo("text/markdown"));
            Assert.That(stored, Is.EqualTo(data));
        });
    }

    [Test]
    public void UploadContent_MissingNode_ThrowsNotFoundException()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        Assert.ThrowsAsync<NotFoundException<Node>>(
            () => svc.UploadContent(99999, "text/plain", new MemoryStream("x"u8.ToArray()), callerId: 0, isAdmin: true));
    }

    // -----------------------------------------------------------------------
    // Timestamps — CreateNode sets Created and LastUpdate
    // -----------------------------------------------------------------------

    [Test]
    public async Task CreateNode_SetsCreatedAndLastUpdate()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        DateTime before = DateTime.UtcNow.AddSeconds(-1);
        NodeDetails node = await Create(svc);
        DateTime after = DateTime.UtcNow.AddSeconds(1);

        Node raw = await fixture.EntityManager.Load<Node>()
                                .Where(n => n.Id == node.Id)
                                .ExecuteEntityAsync();

        Assert.Multiple(() => {
            Assert.That(raw.Created, Is.GreaterThanOrEqualTo(before), "Created must be >= time before insert");
            Assert.That(raw.Created, Is.LessThanOrEqualTo(after), "Created must be <= time after insert");
            Assert.That(raw.LastUpdate, Is.EqualTo(raw.Created), "LastUpdate must equal Created on a fresh node");
        });
    }

    // -----------------------------------------------------------------------
    // Timestamps — Patch moves LastUpdate, leaves Created unchanged
    // -----------------------------------------------------------------------

    [Test]
    public async Task Patch_MovesLastUpdate_LeavesCreatedUnchanged()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await Create(svc);
        Node before = await fixture.EntityManager.Load<Node>()
                                   .Where(n => n.Id == node.Id)
                                   .ExecuteEntityAsync();

        await svc.Patch(node.Id, [new PatchOperation { Op = "replace", Path = "/status", Value = "open" }], callerId: 0, isAdmin: true, CancellationToken.None);

        Node after = await fixture.EntityManager.Load<Node>()
                                  .Where(n => n.Id == node.Id)
                                  .ExecuteEntityAsync();

        Assert.Multiple(() => {
            Assert.That(after.Created, Is.EqualTo(before.Created), "Created must not change after Patch");
            Assert.That(after.LastUpdate, Is.GreaterThanOrEqualTo(before.LastUpdate), "LastUpdate must advance on Patch");
        });
    }

    // -----------------------------------------------------------------------
    // Timestamps — UploadContent moves LastUpdate, leaves Created unchanged
    // -----------------------------------------------------------------------

    [Test]
    public async Task UploadContent_MovesLastUpdate_LeavesCreatedUnchanged()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await Create(svc);
        Node before = await fixture.EntityManager.Load<Node>()
                                   .Where(n => n.Id == node.Id)
                                   .ExecuteEntityAsync();

        await svc.UploadContent(node.Id, "text/plain", new MemoryStream("hello"u8.ToArray()), callerId: 0, isAdmin: true);

        Node after = await fixture.EntityManager.Load<Node>()
                                  .Where(n => n.Id == node.Id)
                                  .ExecuteEntityAsync();

        Assert.Multiple(() => {
            Assert.That(after.Created, Is.EqualTo(before.Created), "Created must not change after UploadContent");
            Assert.That(after.LastUpdate, Is.GreaterThanOrEqualTo(before.LastUpdate), "LastUpdate must advance on UploadContent");
        });
    }

    // -----------------------------------------------------------------------
    // Timestamps — CreatedFrom is inclusive, CreatedTo is exclusive
    // -----------------------------------------------------------------------

    [Test]
    public async Task ListPaged_CreatedFrom_IsInclusive()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await Create(svc, name: "TimestampTarget");
        Node raw = await fixture.EntityManager.Load<Node>()
                                .Where(n => n.Id == node.Id)
                                .ExecuteEntityAsync();

        long count = await GetPageCount(svc, new NodeFilter { Id = [node.Id], CreatedFrom = raw.Created, Count = 100 });

        Assert.That(count, Is.EqualTo(1), "node at exact CreatedFrom boundary must be included (inclusive)");
    }

    [Test]
    public async Task ListPaged_CreatedTo_IsExclusive()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await Create(svc, name: "TimestampTarget");
        Node raw = await fixture.EntityManager.Load<Node>()
                                .Where(n => n.Id == node.Id)
                                .ExecuteEntityAsync();

        long count = await GetPageCount(svc, new NodeFilter { Id = [node.Id], CreatedTo = raw.Created, Count = 100 });

        Assert.That(count, Is.EqualTo(0), "node at exact CreatedTo boundary must be excluded (exclusive)");
    }

    // -----------------------------------------------------------------------
    // Timestamps — UpdatedFrom is inclusive, UpdatedTo is exclusive
    // -----------------------------------------------------------------------

    [Test]
    public async Task ListPaged_UpdatedFrom_IsInclusive()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await Create(svc, name: "UpdatedTarget");
        await svc.Patch(node.Id, [new PatchOperation { Op = "replace", Path = "/status", Value = "open" }], callerId: 0, isAdmin: true, CancellationToken.None);

        Node raw = await fixture.EntityManager.Load<Node>()
                                .Where(n => n.Id == node.Id)
                                .ExecuteEntityAsync();

        long count = await GetPageCount(svc, new NodeFilter { Id = [node.Id], UpdatedFrom = raw.LastUpdate, Count = 100 });

        Assert.That(count, Is.EqualTo(1), "node at exact UpdatedFrom boundary must be included (inclusive)");
    }

    [Test]
    public async Task ListPaged_UpdatedTo_IsExclusive()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await Create(svc, name: "UpdatedTarget");
        await svc.Patch(node.Id, [new PatchOperation { Op = "replace", Path = "/status", Value = "open" }], callerId: 0, isAdmin: true, CancellationToken.None);

        Node raw = await fixture.EntityManager.Load<Node>()
                                .Where(n => n.Id == node.Id)
                                .ExecuteEntityAsync();

        long count = await GetPageCount(svc, new NodeFilter { Id = [node.Id], UpdatedTo = raw.LastUpdate, Count = 100 });

        Assert.That(count, Is.EqualTo(0), "node at exact UpdatedTo boundary must be excluded (exclusive)");
    }

    // -----------------------------------------------------------------------
    // Timestamps — timestamp filters compose with existing Type and Status filters
    // -----------------------------------------------------------------------

    [Test]
    public async Task ListPaged_TimestampFilters_ComposeWithTypeAndStatus()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        DateTime cutoff = DateTime.UtcNow.AddSeconds(-2);

        NodeDetails task1 = await CreateWithStatus(svc, "open", type: "task", name: "TaskOpen");
        NodeDetails bug1 = await CreateWithStatus(svc, "open", type: "bug", name: "BugOpen");

        List<NodeDetails> results = await CollectPage(await svc.ListPaged(new NodeFilter {
            Type = ["task"],
            Status = ["open"],
            CreatedFrom = cutoff,
            Count = 100
        }, callerId: 0, isAdmin: true));

        long[] ids = results.Select(n => n.Id).ToArray();

        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(task1.Id), "task+open node must appear");
            Assert.That(ids, Does.Not.Contain(bug1.Id), "bug node must be excluded by type filter");
        });
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    static async Task<long> GetPageCount(NodeService svc, NodeFilter filter)
    {
        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPaged(filter, callerId: 0, isAdmin: true);
        List<NodeDetails> items = await CollectPage(writer);
        return items.Count;
    }

    static async Task<List<NodeDetails>> CollectPage(Pooshit.AspNetCore.Services.Formatters.DataStream.AsyncPageResponseWriter<NodeDetails> writer)
    {
        // Use a non-disposing wrapper so the stream stays readable after Write() finishes.
        byte[] buffer;
        using (MemoryStream ms = new())
        {
            await writer.Write(ms);
            buffer = ms.ToArray();
        }
        using MemoryStream readStream = new(buffer);
        return await CollectPageFromStream(readStream);
    }

    /// <summary>
    /// The <see cref="AsyncPageResponseWriter{T}"/> serialises to a JSON envelope:
    /// <c>{"result":[...],"total":N}</c>.
    /// Deserialises via Pooshit.Json into a typed <see cref="Pooshit.AspNetCore.Services.Data.Page{T}"/>.
    /// </summary>
    static async Task<List<NodeDetails>> CollectPageFromStream(MemoryStream ms)
    {
        string json = await new StreamReader(ms).ReadToEndAsync();
        Pooshit.AspNetCore.Services.Data.Page<NodeDetails> page =
            Pooshit.Json.Json.Read<Pooshit.AspNetCore.Services.Data.Page<NodeDetails>>(json);
        return page.Result?.ToList() ?? [];
    }

    // -----------------------------------------------------------------------
    // ListLinks — CancellationToken params-object-array trap (bug #305)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Regression test for bug #305.
    ///
    /// Pooshit.Ocelot's ExecuteEntitiesAsync has signature ExecuteEntitiesAsync(params object[]).
    /// Passing a CancellationToken resolves to that overload, making the CT a SQL parameter value.
    /// SQLite silently stringifies it; Npgsql throws:
    ///   "Writing values of System.Threading.CancellationToken is not supported for parameters
    ///    having no NpgsqlDbType or DataTypeName"
    ///
    /// This test uses a DispatchProxy IDBClient spy that rejects CancellationToken parameters
    /// exactly as Npgsql does, so the regression surfaces on SQLite as well.
    ///
    /// NEGATIVE PROOF: restore `operation.ExecuteEntitiesAsync(ct)` at NodeService.cs:525 and
    /// this test fails with InvalidOperationException("CancellationToken SQL parameter detected:
    /// System.Threading.CancellationToken is not a bindable SQL value").
    /// POSITIVE PROOF: with the fix (`operation.ExecuteEntitiesAsync()`) this test passes — the
    /// spy's SetAsync is called with only the IN-clause long parameters.
    /// </summary>
    [Test]
    public async Task ListLinks_WithCancellationToken_DoesNotPassCtToSqlLayer()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        // Create two linked nodes so ListLinks has real rows to return.
        NodeDetails a = await Create(svc, name: "LinkA");
        NodeDetails b = await Create(svc, name: "LinkB");
        await svc.LinkNodes(a.Id, b.Id, callerId: 0, isAdmin: true);

        // Wrap the real IDBClient in a spy that rejects CancellationToken SQL parameters.
        IDBClient spyClient = CancellationTokenRejectingDbClientSpy.Wrap(fixture.EntityManager.DBClient);
        IEntityManager spyManager = new EntityManager(spyClient);

        NodeService spySvc = new(spyManager, DisabledCapability);
        using CancellationTokenSource cts = new();

        AsyncPageResponseWriter<NodeLink> writer = await spySvc.ListLinks(
            [a.Id, b.Id], new Pooshit.AspNetCore.Services.Data.ListFilter { Count = 100 },
            callerId: 0, isAdmin: true, accessibleOrgs: null, cts.Token);

        // Consuming the writer triggers the actual ExecuteEntitiesAsync call.
        // The spy throws if it sees a CancellationToken as a SQL parameter.
        Assert.DoesNotThrowAsync(async () => {
            using MemoryStream ms = new();
            await writer.Write(ms);
        }, "ListLinks must not pass CancellationToken to the SQL layer (bug #305 regression)");
    }
}
