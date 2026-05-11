using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using Pooshit.AspNetCore.Services.Errors.Exceptions;
using Pooshit.AspNetCore.Services.Patches;

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
        => svc.CreateNode(new NodeDetails { Type = type, Name = name });

    static async Task<NodeDetails> CreateWithStatus(NodeService svc, string status, string type = "task", string name = "Test node")
    {
        NodeDetails node = await svc.CreateNode(new NodeDetails { Type = type, Name = name });
        return await svc.Patch(node.Id, new PatchOperation { Op = "replace", Path = "/status", Value = status });
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
        await svc.Delete(node.Id);

        Assert.ThrowsAsync<NotFoundException<Node>>(() => svc.GetNodeById(node.Id)
            .ContinueWith(t => t.Result == null
                ? throw new NotFoundException<Node>(node.Id)
                : t.Result));
    }

    [Test]
    public void Delete_MissingNode_ThrowsNotFoundException()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        Assert.ThrowsAsync<NotFoundException<Node>>(() => svc.Delete(99999));
    }

    [Test]
    public async Task Delete_RemovesAssociatedLinks()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails a = await Create(svc, name: "A");
        NodeDetails b = await Create(svc, name: "B");
        await svc.LinkNodes(a.Id, b.Id);

        await svc.Delete(a.Id);

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
        await svc.LinkNodes(a.Id, b.Id); // stored as SourceId=a, TargetId=b

        await svc.Delete(b.Id); // exercises the TargetId == nodeId branch of the OR predicate

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
        await svc.UploadContent(node.Id, "text/plain", new MemoryStream(content));

        (string contentType, Stream stream) = await svc.GetNodeData(node.Id);
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

        Assert.ThrowsAsync<NotFoundException<Node>>(() => svc.GetNodeData(99999));
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
        await svc.LinkNodes(a.Id, b.Id);

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

        Assert.ThrowsAsync<InvalidOperationException>(() => svc.LinkNodes(1, 1));
    }

    [Test]
    public async Task LinkNodes_MissingSourceNode_ThrowsNotFoundException()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails b = await Create(svc, name: "B");

        Assert.ThrowsAsync<NotFoundException<Node>>(() => svc.LinkNodes(99999, b.Id));
    }

    [Test]
    public async Task LinkNodes_MissingTargetNode_ThrowsNotFoundException()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails a = await Create(svc, name: "A");

        Assert.ThrowsAsync<NotFoundException<Node>>(() => svc.LinkNodes(a.Id, 99999));
    }

    [Test]
    public async Task LinkNodes_DuplicateLink_ThrowsInvalidOperationException()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails a = await Create(svc, name: "A");
        NodeDetails b = await Create(svc, name: "B");
        await svc.LinkNodes(a.Id, b.Id);

        // Same direction
        Assert.ThrowsAsync<InvalidOperationException>(() => svc.LinkNodes(a.Id, b.Id));
    }

    [Test]
    public async Task LinkNodes_DuplicateLinkReverseDirection_ThrowsInvalidOperationException()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails a = await Create(svc, name: "A");
        NodeDetails b = await Create(svc, name: "B");
        await svc.LinkNodes(a.Id, b.Id);

        // Reverse direction should also be treated as a duplicate
        Assert.ThrowsAsync<InvalidOperationException>(() => svc.LinkNodes(b.Id, a.Id));
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
        await svc.LinkNodes(a.Id, b.Id);

        await svc.UnlinkNodes(a.Id, b.Id);

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
        await svc.LinkNodes(a.Id, b.Id); // stored as (a, b)

        // Unlink from the b→a direction should still work
        await svc.UnlinkNodes(b.Id, a.Id);

        long count = await fixture.EntityManager.Load<NodeLink>(Pooshit.Ocelot.Tokens.DB.Count())
                                  .Where(l => l.SourceId == a.Id && l.TargetId == b.Id)
                                  .ExecuteScalarAsync<long>();
        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public async Task UnlinkNodes_NoLink_ThrowsNotFoundException()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails a = await Create(svc, name: "A");
        NodeDetails b = await Create(svc, name: "B");

        Assert.ThrowsAsync<NotFoundException<NodeLink>>(() => svc.UnlinkNodes(a.Id, b.Id));
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
        await svc.LinkNodes(left.Id, hub.Id);
        await svc.LinkNodes(hub.Id, right.Id);

        // Query for nodes linked to hub — should return left and right, not hub itself
        var writer = await svc.ListPaged(new NodeFilter { LinkedTo = [hub.Id], Count = 100 });
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

        var page1Writer = await svc.ListPaged(new NodeFilter { Count = 3 });
        List<NodeDetails> page1 = await CollectPage(page1Writer);

        var page2Writer = await svc.ListPaged(new NodeFilter { Count = 3, Continue = 3 });
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
        var writer = await svc.ListPaged(new NodeFilter
        {
            Count = 100,
            Sort = "name",
            Descending = true
        });
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

        Assert.ThrowsAsync<System.Collections.Generic.KeyNotFoundException>(
            () => svc.ListPaged(new NodeFilter { Count = 10, Sort = "node.name" }));
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
            () => svc.Patch(99999, new PatchOperation { Op = "replace", Path = "/status", Value = "open" }));
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
        NodeDetails result = await svc.Patch(node.Id, new PatchOperation { Op = "replace", Path = "/status", Value = "open" });

        Assert.That(result.Status, Is.EqualTo("open"));
    }

    [Test]
    public async Task Patch_ReplaceStatus_OverwritesExistingStatus()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await Create(svc);
        await svc.Patch(node.Id, new PatchOperation { Op = "replace", Path = "/status", Value = "open" });
        NodeDetails result = await svc.Patch(node.Id, new PatchOperation { Op = "replace", Path = "/status", Value = "closed" });

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
        NodeDetails result = await svc.Patch(node.Id, new PatchOperation { Op = "replace", Path = "/name", Value = "UpdatedName" });

        Assert.That(result.Name, Is.EqualTo("UpdatedName"));
    }

    [Test]
    public async Task Patch_ReplaceName_PersistedToDatabase()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await Create(svc, name: "BeforePatch");
        await svc.Patch(node.Id, new PatchOperation { Op = "replace", Path = "/name", Value = "AfterPatch" });

        // Reload via list to confirm DB was written
        var writer = await svc.ListPaged(new NodeFilter { Id = [node.Id], Count = 1 });
        List<NodeDetails> results = await CollectPage(writer);
        Assert.That(results.Single().Name, Is.EqualTo("AfterPatch"));
    }

    [Test]
    public void Patch_ReplaceType_ThrowsPropertyNotFoundException()
    {
        // The PATCH path "/type" does not map to any property on Node — the DB entity
        // stores type as TypeId (long), not as "type". The extension throws
        // PropertyNotFoundException before the [AllowPatch] check is reached.
        // Either way, /type is not patchable and the middleware returns 400.
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        Assert.ThrowsAsync<PropertyNotFoundException>(
            () => svc.Patch(1, new PatchOperation { Op = "replace", Path = "/type", Value = "other" }));
    }

    [Test]
    public async Task Patch_ReplaceStatus_StillWorksAfterNamePatchAdded()
    {
        // Regression: adding [AllowPatch] to Name must not break the existing Status patch path.
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await Create(svc);
        NodeDetails result = await svc.Patch(node.Id, new PatchOperation { Op = "replace", Path = "/status", Value = "in-progress" });

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

        await svc.LinkNodes(project.Id, openTask.Id);
        await svc.LinkNodes(project.Id, closedTask.Id);

        var writer = await svc.ListPaged(new NodeFilter { LinkedTo = [project.Id], Status = ["open"], Count = 100 });
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

        var writer = await svc.ListPaged(new NodeFilter { NoStatus = true, Count = 100 });
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

        var writer = await svc.ListPaged(new NodeFilter { NoStatus = true, Count = 100 });
        List<NodeDetails> results = await CollectPage(writer);

        Assert.That(results.Select(n => n.Id), Does.Not.Contain(withStatus.Id));
    }

    [Test]
    public async Task ListPaged_StatusAppearsInDefaultFields()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails node = await Create(svc, name: "CheckFields");
        await svc.Patch(node.Id, new PatchOperation { Op = "replace", Path = "/status", Value = "open" });

        var writer = await svc.ListPaged(new NodeFilter { Id = [node.Id], Count = 100 });
        List<NodeDetails> results = await CollectPage(writer);

        Assert.That(results.Single().Status, Is.EqualTo("open"));
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

        await svc.UploadContent(node.Id, "text/markdown", new MemoryStream(data));

        (string ct, Stream stream) = await svc.GetNodeData(node.Id);
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
            () => svc.UploadContent(99999, "text/plain", new MemoryStream("x"u8.ToArray())));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    static async Task<long> GetPageCount(NodeService svc, NodeFilter filter)
    {
        var writer = await svc.ListPaged(filter);
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
}
