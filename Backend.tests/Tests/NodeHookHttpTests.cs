using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Models.Messages;
using Backend.Models.Nodes;
using Backend.Models.Users;
using Backend.Services.Embeddings;
using Backend.Services.Layout;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Tokens;

namespace Backend.tests.Tests;

/// <summary>node hook API and transactional mutation tests</summary>
[TestFixture]
public class NodeHookHttpTests {
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    JwtAuthFixture fixture = null!;
    IEntityManager db = null!;
    NodeService service = null!;
    HttpClient owner = null!;
    HttpClient reader = null!;
    HttpClient admin = null!;
    long ownerId;
    long readerId;
    long adminId;
    long nodeId;

    /// <summary>setup</summary>
    [SetUp]
    public async Task Setup() {
        fixture = new();
        db = fixture.EntityManager;
        service = new(db, new EmbeddingCapability(false), EmbeddingProducers.Function);
        ownerId = await User("owner");
        readerId = await User("reader");
        adminId = await User("admin", "[\"admin\",\"write\",\"read\"]");
        owner = Client(ownerId);
        reader = Client(readerId);
        admin = Client(adminId);
        nodeId = (await service.CreateNode(new NodeDetails { Name = "private-name", Status = "open" }, ownerId)).Id;
    }

    /// <summary>teardown</summary>
    [TearDown]
    public void Teardown() {
        owner.Dispose();
        reader.Dispose();
        admin.Dispose();
        fixture.Dispose();
    }

    async Task<long> User(string name, string permissions = "[\"write\",\"read\"]") =>
        await db.Insert<User>().Columns(u => u.Name, u => u.Enabled, u => u.Permissions, u => u.CreatedAt)
                .Values(name, true, permissions, DateTime.UtcNow).ReturnID().ExecuteAsync();

    HttpClient Client(long id) {
        HttpClient client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.MintToken(userId: id));
        return client;
    }

    Task<NodeHook> Hook(long? userId = null, string tag = "route", long? watched = null, NodeHookEvents events = NodeHookEvents.All) =>
        service.RegisterHook(new NodeHook { NodeId = watched ?? nodeId, UserId = userId ?? readerId, Tag = tag, Events = events }, adminId, true);

    async Task<Message[]> Messages(long? recipient = null) {
        long id = recipient ?? readerId;
        List<Message> messages = [];
        await foreach (Message message in db.Load<Message>().Where(m => m.RecipientId == id).ExecuteEntitiesAsync()) messages.Add(message);
        return messages.ToArray();
    }

    Task Clear() => db.Delete<Message>().ExecuteAsync();

    Task Patch(string path, object value) => service.Patch(nodeId,
        [new PatchOperation { Op = "replace", Path = path, Value = value }], ownerId, false, default);

    static async Task AssertStatus(HttpResponseMessage response, HttpStatusCode expected) {
        Assert.That(response.StatusCode, Is.EqualTo(expected), await response.Content.ReadAsStringAsync());
    }

    /// <summary>register hook idempotent tuple allows multiple tags and users</summary>
    [Test]
    public async Task RegisterHook_IdempotentTuple_AllowsMultipleTagsAndUsers() {
        object payload = new { nodeId, userId = readerId, tag = " route " };
        await AssertStatus(await reader.PostAsJsonAsync("/api/nodehooks", payload), HttpStatusCode.OK);
        await AssertStatus(await reader.PostAsJsonAsync("/api/nodehooks", payload), HttpStatusCode.OK);
        await Hook(tag: "Route");
        await Hook(ownerId);
        NodeHook[] hooks = (await reader.GetFromJsonAsync<NodeHook[]>("/api/nodehooks", Json))!;
        Assert.That(hooks.Select(h => h.Tag), Is.EquivalentTo(new[] { "route", "Route" }));
        await Patch("/status", "closed");
        Assert.That(await Messages(), Has.Length.EqualTo(2));
        Assert.That(await Messages(ownerId), Has.Length.EqualTo(1));
    }

    /// <summary>register hook concurrent duplicate is unique</summary>
    [Test]
    public async Task RegisterHook_ConcurrentDuplicate_IsUnique() {
        Task<HttpResponseMessage>[] requests = Enumerable.Range(0, 8).Select(_ => reader.PostAsJsonAsync("/api/nodehooks", new { nodeId, userId = readerId, tag = "same" })).ToArray();
        foreach (HttpResponseMessage response in await Task.WhenAll(requests)) await AssertStatus(response, HttpStatusCode.OK);
        Assert.That(await db.Load<NodeHook>(DB.Count()).ExecuteScalarAsync<long>(), Is.EqualTo(1));
        Assert.CatchAsync(async () => await db.Insert<NodeHook>().Columns(h => h.NodeId, h => h.UserId, h => h.Tag).Values(nodeId, readerId, "same").ExecuteAsync());
    }

    /// <summary>hook management own subscriber or admin only</summary>
    [Test]
    public async Task HookManagement_OwnSubscriberOrAdminOnly() {
        object other = new { nodeId, userId = ownerId, tag = "route" };
        await AssertStatus(await reader.PostAsJsonAsync("/api/nodehooks", other), HttpStatusCode.NotFound);
        await AssertStatus(await reader.GetAsync($"/api/nodehooks?userId={ownerId}"), HttpStatusCode.NotFound);
        await AssertStatus(await reader.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/nodehooks") { Content = JsonContent.Create(other) }), HttpStatusCode.NotFound);
        await AssertStatus(await admin.PostAsJsonAsync("/api/nodehooks", other), HttpStatusCode.OK);
        Assert.That((await admin.GetFromJsonAsync<NodeHook[]>($"/api/nodehooks?userId={ownerId}", Json))!, Has.Length.EqualTo(1));
    }

    /// <summary>registration rejects invalid tags</summary>
    /// <param name="tag">invalid tag value</param>
    [TestCase("")]
    [TestCase(" ")]
    [TestCase("bad[tag")]
    [TestCase("bad]tag")]
    [TestCase("bad\ntag")]
    public async Task RegisterHook_InvalidTag_Returns400(string tag) =>
        await AssertStatus(await reader.PostAsJsonAsync("/api/nodehooks", new { nodeId, userId = readerId, tag }), HttpStatusCode.BadRequest);

    /// <summary>register hook requires recipient read access</summary>
    [Test]
    public async Task RegisterHook_RequiresRecipientReadAccess() {
        await Patch("/access", "None");
        await AssertStatus(await admin.PostAsJsonAsync("/api/nodehooks", new { nodeId, userId = readerId, tag = "route" }), HttpStatusCode.NotFound);
        await AssertStatus(await admin.PostAsJsonAsync("/api/nodehooks", new { nodeId, userId = adminId, tag = "route" }), HttpStatusCode.OK);
        await AssertStatus(await owner.PostAsJsonAsync("/api/nodehooks", new { nodeId, userId = ownerId, tag = "route" }), HttpStatusCode.OK);
    }

    /// <summary>node patch status message contains old and new and author zero</summary>
    [Test]
    public async Task NodePatch_StatusMessage_ContainsOldAndNew_AndAuthorZero() {
        await Hook();
        await Patch("/status", "closed");
        Message message = (await Messages()).Single();
        Assert.Multiple(() => {
            Assert.That(message.AuthorId, Is.Zero);
            Assert.That(message.Subject, Is.EqualTo($"[route] Node #{nodeId} changed"));
            Assert.That(message.Body, Does.Contain("Status changed: open → closed"));
            Assert.That(message.Body, Does.Not.Contain("private-name"));
        });
        HttpResponseMessage inbox = await reader.GetAsync("/api/messages");
        await AssertStatus(inbox, HttpStatusCode.OK);
        Assert.That(await inbox.Content.ReadAsStringAsync(), Does.Contain("\"authorId\":0"));
    }

    /// <summary>node patch same value and empty emit nothing</summary>
    [Test]
    public async Task NodePatch_SameValueAndEmpty_EmitNothing() {
        await Hook();
        await Patch("/status", "open");
        await service.Patch(nodeId, [], ownerId, false, default);
        Assert.That(await Messages(), Is.Empty);
    }

    /// <summary>node patch all metadata changes emit compact summary</summary>
    [Test]
    public async Task NodePatch_AllMetadataChanges_EmitCompactSummary() {
        await Hook();
        await service.Patch(nodeId, [
            new() { Op = "replace", Path = "/name", Value = "new-name" },
            new() { Op = "replace", Path = "/type", Value = "task" },
            new() { Op = "replace", Path = "/severity", Value = 3 },
            new() { Op = "replace", Path = "/refinement", Value = "ready" },
            new() { Op = "replace", Path = "/rootNodeId", Value = 7L },
            new() { Op = "replace", Path = "/substance", Value = "secret-substance" },
            new() { Op = "replace", Path = "/skipEmbedding", Value = true },
            new() { Op = "replace", Path = "/ownerId", Value = readerId },
            new() { Op = "replace", Path = "/access", Value = "Read" },
            new() { Op = "replace", Path = "/x", Value = 5.0 }
        ], adminId, true, default);
        string body = (await Messages()).Single().Body;
        foreach (string field in new[] { "Name", "Type", "Severity", "Refinement", "Root node", "Substance", "Embedding preference", "Owner", "Access", "Position" })
            Assert.That(body, Does.Contain(field + " changed"));
        Assert.That(body, Does.Not.Contain("secret-substance"));
    }

    /// <summary>content writes upload and edit are compact and noop aware</summary>
    [Test]
    public async Task ContentWrites_UploadAndEdit_AreCompactAndNoopAware() {
        await Hook();
        using MemoryStream data = new(Encoding.UTF8.GetBytes("secret-body"));
        await service.UploadContent(nodeId, "text/plain", data, ownerId, false);
        Assert.That((await Messages()).Single().Body, Does.Contain("Content changed").And.Not.Contain("secret-body"));
        await Clear();
        data.Position = 0;
        await service.UploadContent(nodeId, "text/plain", data, ownerId, false);
        Assert.That(await Messages(), Is.Empty);
        await service.PatchContent(nodeId, [new ContentEdit { Unit = ContentEditUnit.Char, Start = 0, Length = 6, Value = "edited" }], ownerId, false, default);
        Assert.That((await Messages()).Single().Body, Does.Contain("Content changed").And.Not.Contain("edited-body"));
    }

    /// <summary>content write substance clear is reported</summary>
    [Test]
    public async Task ContentWrite_SubstanceClear_IsReported() {
        using MemoryStream data = new(Encoding.UTF8.GetBytes("body"));
        await service.UploadContent(nodeId, "text/plain", data, ownerId, false);
        await Patch("/substance", "summary");
        await Hook();
        data.Position = 0;
        await service.UploadContent(nodeId, "text/plain", data, ownerId, false);
        Assert.That((await Messages()).Single().Body, Does.Contain("Substance changed").And.Not.Contain("Content changed"));
    }

    /// <summary>access revocation generic notice then suppresses until readable</summary>
    [Test]
    public async Task AccessRevocation_GenericNotice_ThenSuppressesUntilReadable() {
        await Hook();
        await service.Patch(nodeId, [new() { Op = "replace", Path = "/status", Value = "secret" }, new() { Op = "replace", Path = "/access", Value = "None" }], ownerId, false, default);
        Assert.That((await Messages()).Single().Body, Is.EqualTo($"Access changed for node #{nodeId}."));
        await Clear();
        await Patch("/status", "more-secret");
        Assert.That(await Messages(), Is.Empty);
        Assert.That((await reader.GetFromJsonAsync<NodeHook[]>("/api/nodehooks", Json))!, Has.Length.EqualTo(1));
        await Patch("/access", "Read, Write");
        Assert.That((await Messages()).Single().Body, Does.Contain("Access changed").And.Not.Contain("secret"));
        await reader.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/nodehooks") { Content = JsonContent.Create(new { nodeId, userId = readerId, tag = "route" }) });
        Assert.That((await reader.GetFromJsonAsync<NodeHook[]>("/api/nodehooks", Json))!, Is.Empty);
    }

    /// <summary>disabled recipient is skipped</summary>
    [Test]
    public async Task DisabledRecipient_IsSkipped() {
        await Hook();
        await db.Update<User>().Set(u => u.Enabled == false).Where(u => u.Id == readerId).ExecuteAsync();
        await Patch("/status", "closed");
        Assert.That(await Messages(), Is.Empty);
    }

    /// <summary>link changes notify both endpoints and suppress noops</summary>
    [Test]
    public async Task LinkChanges_NotifyBothEndpoints_AndSuppressNoops() {
        long target = (await service.CreateNode(new NodeDetails { Name = "hidden-target", Access = NodeAccess.None, X = 100, Y = 100 }, ownerId)).Id;
        await Hook();
        await Hook(ownerId, watched: target);
        await service.LinkNodes(nodeId, target, ownerId, false, context: "private-context");
        Assert.That((await Messages()).Single().Body, Does.Contain("Link added").And.Contain("Position changed").And.Not.Contain("hidden-target").And.Not.Contain("private-context").And.Not.Contain($"#{target}"));
        Assert.That((await Messages(ownerId)).Single().Body, Does.Contain("Link added"));
        await Clear();
        await service.LinkNodes(nodeId, target, ownerId, false);
        await service.PatchLink(nodeId, target, [new() { Op = "replace", Path = "/context", Value = "private-context" }], ownerId, false, default);
        Assert.That(await Messages(), Is.Empty);
        await service.PatchLink(nodeId, target, [new() { Op = "replace", Path = "/context", Value = "changed" }], ownerId, false, default);
        Assert.That((await Messages()).Single().Body, Does.Contain("Link changed"));
        await Clear();
        await service.UnlinkNodes(nodeId, target, ownerId, false);
        Assert.That((await Messages()).Single().Body, Does.Contain("Link removed"));
        await Clear();
        await service.UnlinkNodes(nodeId, target, ownerId, false);
        Assert.That(await Messages(), Is.Empty);
    }

    /// <summary>create links notify existing endpoint once</summary>
    [Test]
    public async Task CreateLinks_NotifyExistingEndpointOnce() {
        await Hook();
        await service.CreateNode(new NodeDetails { Name = "new", Links = [nodeId, nodeId] }, ownerId);
        Assert.That((await Messages()).Single().Body, Does.Contain("Link added"));
    }

    /// <summary>delete node final notice removes hooks and notifies neighbors</summary>
    [Test]
    public async Task DeleteNode_FinalNotice_RemovesHooksAndNotifiesNeighbors() {
        long target = (await service.CreateNode(new NodeDetails { Name = "neighbor", Links = [nodeId] }, ownerId)).Id;
        await Hook();
        await Hook(watched: target);
        await service.Delete(nodeId, ownerId, false);
        Message[] messages = await Messages();
        Assert.That(messages, Has.Length.EqualTo(2));
        Assert.That(messages.Any(m => m.Body.Contains($"Node #{nodeId} deleted")), Is.True);
        Assert.That(messages.Any(m => m.Body.Contains("Link removed")), Is.True);
        Assert.That(await service.ListHooks(nodeId, readerId, readerId, false), Is.Empty);
    }

    /// <summary>layout position change notifies once</summary>
    [Test]
    public async Task LayoutPositionChange_NotifiesOnce() {
        await Hook();
        LayoutNodesService layout = new(db, NullLogger<LayoutNodesService>.Instance, service);
        await layout.RunAsync();
        Assert.That((await Messages()).Single().Body, Does.Contain("Position changed"));
        await Clear();
        await layout.RunAsync();
        Assert.That(await Messages(), Is.Empty);
    }

    /// <summary>message insert failure rolls back mutation and messages</summary>
    [Test]
    public async Task MessageInsertFailure_RollsBackMutationAndMessages() {
        await Hook();
        await Hook(tag: "second");
        await db.DBClient.NonQueryAsync("CREATE TRIGGER reject_message BEFORE INSERT ON message WHEN NEW.subject LIKE '[second]%' BEGIN SELECT RAISE(ABORT, 'test insertion failure'); END");
        Assert.CatchAsync(async () => await Patch("/status", "closed"));
        Assert.That((await service.GetNodeById(nodeId, ownerId, false)).Status, Is.EqualTo("open"));
        Assert.That(await Messages(), Is.Empty);
    }

    /// <summary>hook removal remains available after access revocation</summary>
    [Test]
    public async Task HookRemoval_RemainsAvailableAfterAccessRevocation() {
        await Hook();
        await Patch("/access", "None");
        object payload = new { nodeId, userId = readerId, tag = "route" };
        await AssertStatus(await reader.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/nodehooks") { Content = JsonContent.Create(payload) }), HttpStatusCode.OK);
        await AssertStatus(await reader.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/nodehooks") { Content = JsonContent.Create(payload) }), HttpStatusCode.OK);
        Assert.That((await reader.GetFromJsonAsync<NodeHook[]>("/api/nodehooks", Json))!, Is.Empty);
    }

    /// <summary>derived timestamp backfill does not notify</summary>
    [Test]
    public async Task DerivedTimestampBackfill_DoesNotNotify() {
        await Hook();
        await db.Update<Node>().Set(n => n.Created == DateTime.MinValue).Where(n => n.Id == nodeId).ExecuteAsync();
        await new Backend.Init.DatabaseModelService(db, NullLogger<Backend.Init.DatabaseModelService>.Instance).StartAsync(default);
        Assert.That(await Messages(), Is.Empty);
    }

    /// <summary>status null and long values are bounded</summary>
    [Test]
    public async Task StatusNullAndLongValues_AreBounded() {
        await Hook();
        await Patch("/status", null!);
        Assert.That((await Messages()).Single().Body, Does.Contain("open → (unset)"));
        await Clear();
        await Patch("/status", new string('x', 300));
        string body = (await Messages()).Single().Body;
        Assert.That(body, Does.Contain("(unset) → ").And.Contain("…"));
        Assert.That(body.Length, Is.LessThan(200));
    }

    /// <summary>content type change notifies and same edit does not</summary>
    [Test]
    public async Task ContentTypeChange_Notifies_AndSameEditDoesNot() {
        using MemoryStream data = new(Encoding.UTF8.GetBytes("body"));
        await service.UploadContent(nodeId, "text/plain", data, ownerId, false);
        await Hook();
        await service.PatchContent(nodeId, [new ContentEdit { Unit = ContentEditUnit.Char, Start = 0, Length = 4, Value = "body" }], ownerId, false, default);
        Assert.That(await Messages(), Is.Empty);
        data.Position = 0;
        await service.UploadContent(nodeId, "text/markdown", data, ownerId, false);
        Assert.That((await Messages()).Single().Body, Does.Contain("Content changed"));
    }

    async Task<Message[]> ExerciseEveryKind(long target, string tag, NodeHookEvents? events) {
        await Patch("/x", 5.0);
        if (events != null) await Hook(tag: tag, events: events.Value);
        await Hook(ownerId, tag: "control");
        using MemoryStream data = new(Encoding.UTF8.GetBytes("body"));
        await service.UploadContent(nodeId, "text/plain", data, ownerId, false);
        await service.LinkNodes(nodeId, target, ownerId, false);
        await Patch("/name", "renamed");
        await Patch("/status", "closed");
        await Patch("/x", 9.0);
        await service.Patch(nodeId, [new() { Op = "replace", Path = "/ownerId", Value = adminId }], adminId, true, default);
        Assert.That(await Messages(ownerId), Has.Length.EqualTo(6));
        return await Messages();
    }

    async Task<long> PositionedTarget() =>
        (await service.CreateNode(new NodeDetails { Name = "target", X = 100, Y = 100 }, ownerId)).Id;

    /// <summary>omitted or zero events are stored as all and notify every kind</summary>
    /// <param name="events">events value sent or null when omitted</param>
    [TestCase(null)]
    [TestCase(0)]
    public async Task RegisterHook_OmittedOrZeroEvents_StoresAllAndNotifiesEveryKind(int? events) {
        object payload = events == null
            ? new { nodeId, userId = readerId, tag = "route" }
            : new { nodeId, userId = readerId, tag = "route", events };
        await AssertStatus(await reader.PostAsJsonAsync("/api/nodehooks", payload), HttpStatusCode.OK);
        Assert.That((await service.ListHooks(nodeId, readerId, readerId, false)).Single().Events, Is.EqualTo(NodeHookEvents.All));
        Message[] messages = await ExerciseEveryKind(await PositionedTarget(), "unused", null);
        Assert.That(messages.Count(m => m.Subject.StartsWith("[route]")), Is.EqualTo(7));
    }

    /// <summary>status only hook ignores other changes and fires once on status</summary>
    [Test]
    public async Task Patch_StatusOnlyHook_NotifiesOnlyOnStatusChange() {
        Message[] messages = await ExerciseEveryKind(await PositionedTarget(), "waiter", NodeHookEvents.Status);
        Assert.That(messages.Where(m => m.Subject.StartsWith("[waiter]")).Single().Body, Does.Contain("Status changed: open → closed"));
        Assert.That(messages.Count(m => m.Subject.StartsWith("[waiter]")), Is.EqualTo(1));
    }

    /// <summary>each kind selects only its own changes</summary>
    /// <param name="events">subscribed kind</param>
    /// <param name="count">number of delivered messages</param>
    /// <param name="expected">text a delivered message contains</param>
    [TestCase(NodeHookEvents.Content, 1, "Content changed")]
    [TestCase(NodeHookEvents.Links, 1, "Link added")]
    [TestCase(NodeHookEvents.Metadata, 3, "Name changed")]
    public async Task Patch_EachKindFilter_SelectsOnlyItsChanges(NodeHookEvents events, int count, string expected) {
        Message[] messages = (await ExerciseEveryKind(await PositionedTarget(), "waiter", events)).Where(m => m.Subject.StartsWith("[waiter]")).ToArray();
        Assert.That(messages, Has.Length.EqualTo(count));
        Assert.That(messages.Any(m => m.Body.Contains(expected)), Is.True);
    }

    /// <summary>matched operation delivers the full consolidated body</summary>
    [Test]
    public async Task Patch_FilteredHookMatchedOperation_BodyListsAllChanges() {
        await Hook(events: NodeHookEvents.Status);
        await service.Patch(nodeId, [new() { Op = "replace", Path = "/status", Value = "closed" }, new() { Op = "replace", Path = "/name", Value = "renamed" }], ownerId, false, default);
        Assert.That((await Messages()).Single().Body, Does.Contain("Status changed: open → closed").And.Contain("Name changed"));
    }

    /// <summary>deletion reaches a filtered hook</summary>
    [Test]
    public async Task Delete_FilteredHook_AlwaysSendsFinalNotice() {
        await Hook(events: NodeHookEvents.Content);
        await service.Delete(nodeId, ownerId, false);
        Assert.That((await Messages()).Single().Body, Does.Contain($"Node #{nodeId} deleted"));
    }

    /// <summary>read loss reaches a filtered hook while a readable access change does not</summary>
    [Test]
    public async Task Patch_StatusOnlyHookAccessChange_NotifiesOnlyOnReadLoss() {
        await Hook(events: NodeHookEvents.Status);
        await Hook(ownerId, tag: "control");
        await Patch("/access", "Read");
        Assert.That(await Messages(ownerId), Has.Length.EqualTo(1));
        Assert.That(await Messages(), Is.Empty);
        await Patch("/access", "None");
        Assert.That((await Messages()).Single().Body, Is.EqualTo($"Access changed for node #{nodeId}."));
    }

    /// <summary>owner change that removes read access reaches a status only hook</summary>
    [Test]
    public async Task Patch_StatusOnlyHookOwnerChange_NotifiesOnReadLoss() {
        await service.Patch(nodeId, [new() { Op = "replace", Path = "/ownerId", Value = readerId }, new() { Op = "replace", Path = "/access", Value = "None" }], adminId, true, default);
        await Hook(events: NodeHookEvents.Status);
        await service.Patch(nodeId, [new() { Op = "replace", Path = "/ownerId", Value = adminId }], adminId, true, default);
        Assert.That((await Messages()).Single().Body, Is.EqualTo($"Access changed for node #{nodeId}."));
    }

    /// <summary>re-registering a tuple replaces its filter without a duplicate row</summary>
    [Test]
    public async Task RegisterHook_ReRegisterWithNewEvents_UpdatesFilterInPlace() {
        await Hook(events: NodeHookEvents.Content);
        NodeHook stored = await Hook(events: NodeHookEvents.Status);
        NodeHook[] hooks = await service.ListHooks(nodeId, readerId, readerId, false);
        Assert.That(hooks, Has.Length.EqualTo(1));
        Assert.That(hooks.Single().Events, Is.EqualTo(NodeHookEvents.Status));
        Assert.That(stored.Events, Is.EqualTo(NodeHookEvents.Status));
        using MemoryStream data = new(Encoding.UTF8.GetBytes("body"));
        await service.UploadContent(nodeId, "text/plain", data, ownerId, false);
        Assert.That(await Messages(), Is.Empty);
        await Patch("/status", "closed");
        Assert.That(await Messages(), Has.Length.EqualTo(1));
    }

    /// <summary>unknown event flags are rejected</summary>
    /// <param name="events">invalid mask</param>
    [TestCase(16)]
    [TestCase(-1)]
    public async Task RegisterHook_UnknownEventFlags_Returns400(int events) {
        await AssertStatus(await reader.PostAsJsonAsync("/api/nodehooks", new { nodeId, userId = readerId, tag = "route", events }), HttpStatusCode.BadRequest);
        Assert.That(await service.ListHooks(nodeId, readerId, readerId, false), Is.Empty);
    }
}
