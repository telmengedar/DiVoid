using Pooshit.Ocelot.Tokens;
using Microsoft.AspNetCore.Hosting;
using Backend.Models.Messages;
using Pooshit.Ocelot.Clients;
using Pooshit.AspNetCore.Services.Patches;
using Microsoft.Data.Sqlite;
using Backend.Services.Embeddings;
using Backend.Services.Users;
using Backend.Services.NodeChanges;
using Backend.Errors.Exceptions;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Backend.Models.Auth;
using Backend.Models.Nodes;
using Backend.Models.Users;
using Backend.Services.Auth;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Pooshit.Ocelot.Entities;

namespace Backend.tests.Tests;

/// <summary>Exercises the authenticated producer-driven graph feed through actual HTTP mutations.</summary>
[TestFixture]
public class NodeChangeFeedTests {
    /// <summary>Checks a newly created scoped type wakes a real stream without a prior node hook.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Test]
    public async Task NodeChanges_CreateWithoutLinksAndInitialMembership_Wakes() {
        using JwtAuthFixture fixture = new();
        long ownerId = await fixture.EntityManager.Insert<User>().Columns(user => user.Name, user => user.Enabled, user => user.CreatedAt)
            .Values("feed-owner", true, DateTime.UtcNow).ReturnID().ExecuteAsync();
        INodeService nodes = fixture.Services.GetRequiredService<INodeService>();
        long root = (await nodes.CreateNode(new NodeDetails { Name = "private-root", Access = NodeAccess.None }, ownerId)).Id;
        ApiKeyDetails key = await fixture.Services.GetRequiredService<IApiKeyService>().CreateApiKey(new ApiKeyParameters {
            UserId = ownerId, Permissions = ["read", "write"]
        });
        using HttpClient client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.PlaintextKey);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using HttpResponseMessage stream = await client.GetAsync($"/api/v1/nodechanges?rootNodeId={root}&type=configuration", HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        Assert.That(stream.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using StreamReader reader = new(await stream.Content.ReadAsStreamAsync(deadline.Token));
        Assert.That(await reader.ReadLineAsync(deadline.Token), Is.EqualTo("event:ready"));
        Assert.That(await reader.ReadLineAsync(deadline.Token), Is.EqualTo("data:{\"version\":1,\"coverage\":\"this-process\",\"replay\":false,\"resyncRequired\":true}"));
        Assert.That(await reader.ReadLineAsync(deadline.Token), Is.Empty);
        using HttpResponseMessage created = await client.PostAsJsonAsync("/api/nodes", new NodeDetails {
            Name = "private-config-value", Type = "configuration", RootNodeId = root, Access = NodeAccess.None
        }, deadline.Token);
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await reader.ReadLineAsync(deadline.Token), Is.EqualTo("event:invalidate"));
        Assert.That(await reader.ReadLineAsync(deadline.Token), Is.EqualTo("data:{}"));
        Assert.That(await reader.ReadLineAsync(deadline.Token), Is.Empty);
    }
    /// <summary>Checks the actual HTTP producer and stream on each qualified provider.</summary>
    /// <param name="postgres">Whether PostgreSQL is selected.</param>
    /// <param name="sharedCache">Whether SQLite shared-cache locking is selected.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase(false, false), TestCase(false, true), TestCase(true, false)]
    public async Task NodeChangesHttp_RealProducerAndEmptyFrames(bool postgres, bool sharedCache) {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(postgres, sharedCache);
        await using Connection stream = await Connect(fixture, "&type=configuration");
        Assert.That(stream.Response.Content.Headers.ContentType!.MediaType, Is.EqualTo("text/event-stream"));
        Assert.That(stream.Response.Headers.CacheControl!.NoStore, Is.True);
        Assert.That(stream.Response.Headers.GetValues("X-Accel-Buffering"), Is.EqualTo(new[] { "no" }));
        using HttpClient client = fixture.Client();
        using HttpResponseMessage created = await client.PostAsJsonAsync("/api/nodes", new NodeDetails {
            Name = "private-value-canary", Type = "configuration", RootNodeId = fixture.RootId, Access = NodeAccess.None
        });
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await stream.Frame(), Is.EqualTo("event:invalidate\ndata:{}"));
    }

    /// <summary>Checks exact root/type selection and entry or exit independently of category filtering.</summary>
    /// <param name="postgres">Whether PostgreSQL is selected.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase(false), TestCase(true)]
    public async Task NodeChangesFilters_RootIdsTypesAndLifecycle(bool postgres) {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(postgres);
        await using NodeChangeSubscription sub = await Subscribe(fixture, new NodeChangeFilter { RootNodeId = fixture.RootId, Type = ["selected"], Events = NodeHookEvents.Status });
        long id = await fixture.MemberAsync("different");
        Assert.That(sub.Reader.TryRead(out _), Is.False);
        await fixture.Nodes.Patch(id, [Patch("/type", "selected")], fixture.OwnerId, false, default);
        Dirty(sub);
        await fixture.Nodes.Patch(id, [Patch("/name", "changed")], fixture.OwnerId, false, default);
        Assert.That(sub.Reader.TryRead(out _), Is.False);
        await fixture.Nodes.Patch(id, [Patch("/status", "private-status")], fixture.OwnerId, false, default);
        Dirty(sub);
        long other = (await fixture.Nodes.CreateNode(new NodeDetails { Name = "other-root" }, fixture.OwnerId)).Id;
        await fixture.Nodes.Patch(id, [Patch("/rootNodeId", other)], fixture.OwnerId, false, default);
        Dirty(sub);
        await fixture.Nodes.Patch(id, [Patch("/status", "outside")], fixture.OwnerId, false, default);
        Assert.That(sub.Reader.TryRead(out _), Is.False);
        await fixture.Nodes.Patch(id, [Patch("/rootNodeId", fixture.RootId)], fixture.OwnerId, false, default);
        Dirty(sub);
        await fixture.Nodes.Delete(id, fixture.OwnerId, false);
        Dirty(sub);
    }

    /// <summary>Checks initial links, content, edge changes, positions and no-op suppression.</summary>
    /// <param name="postgres">Whether PostgreSQL is selected.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase(false), TestCase(true)]
    public async Task NodeChanges_ActualMutationPathCoverage(bool postgres) {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(postgres);
        long group = await fixture.MemberAsync("group");
        await using NodeChangeSubscription sub = await Subscribe(fixture, new NodeChangeFilter { RootNodeId = fixture.RootId });
        long id = (await fixture.Nodes.CreateNode(new NodeDetails { Name = "member", RootNodeId = fixture.RootId, Links = [group] }, fixture.OwnerId)).Id;
        Dirty(sub);
        Assert.That(sub.Reader.TryRead(out _), Is.False);
        await fixture.Nodes.LinkNodes(id, group, fixture.OwnerId, false);
        Assert.That(sub.Reader.TryRead(out _), Is.False);
        await fixture.Nodes.Patch(id, [Patch("/name", "member")], fixture.OwnerId, false, default);
        Assert.That(sub.Reader.TryRead(out _), Is.False);
        using MemoryStream body = new("private-content"u8.ToArray());
        await fixture.Nodes.UploadContent(id, "text/plain", body, fixture.OwnerId, false);
        Dirty(sub);
        using MemoryStream identical = new("private-content"u8.ToArray());
        await fixture.Nodes.UploadContent(id, "text/plain", identical, fixture.OwnerId, false);
        Assert.That(sub.Reader.TryRead(out _), Is.False);
        await fixture.Nodes.PatchContent(id, [new ContentEdit { Unit = ContentEditUnit.Char, Start = 0, Length = 7, Value = "edited" }], fixture.OwnerId, false, default);
        Dirty(sub);
        await fixture.Nodes.PatchLink(id, group, [Patch("/context", "private-edge")], fixture.OwnerId, false, default);
        Dirty(sub);
        await fixture.Nodes.UnlinkNodes(id, group, fixture.OwnerId, false);
        Dirty(sub);
        await fixture.Nodes.UnlinkNodes(id, group, fixture.OwnerId, false);
        Assert.That(sub.Reader.TryRead(out _), Is.False);
        long positioned = await fixture.MemberAsync();
        Dirty(sub);
        await fixture.Nodes.PositionUnpositionedNode(positioned, 20, 30, default);
        Dirty(sub);
        await fixture.Nodes.PositionUnpositionedNode(positioned, 20, 30, default);
        Assert.That(sub.Reader.TryRead(out _), Is.False);
        await fixture.Nodes.LinkNodes(id, group, fixture.OwnerId, false);
        Dirty(sub);
        await fixture.Nodes.Delete(id, fixture.OwnerId, false);
        Dirty(sub);
    }

    /// <summary>Checks API keys cannot inherit DB-user admin grants and JWT identities cannot select key authority.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test]
    public async Task NodeChangesHttp_RequiresCurrentApiKeyReadAndReadableRoot() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync();
        long dbAdmin = await fixture.AddUserAsync("[\"admin\"]");
        ApiKeyDetails limited = await fixture.AddKeyAsync(dbAdmin, ["read"]);
        using HttpClient restricted = fixture.Client(limited);
        using HttpResponseMessage invisible = await restricted.GetAsync($"/api/v1/nodechanges?rootNodeId={fixture.RootId}");
        Assert.That(invisible.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        using HttpClient jwt = fixture.Host.CreateClient();
        jwt.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Host.MintToken(userId: dbAdmin));
        using HttpResponseMessage wrongIdentity = await jwt.GetAsync($"/api/v1/nodechanges?rootNodeId={fixture.RootId}");
        Assert.That(wrongIdentity.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        ApiKeyDetails admin = await fixture.AddKeyAsync(dbAdmin, ["admin"]);
        await using Connection authorized = await Connect(fixture, "", admin);
        using HttpClient absent = fixture.Host.CreateClient();
        using HttpResponseMessage denied = await absent.GetAsync($"/api/v1/nodechanges?rootNodeId={fixture.RootId}");
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    /// <summary>Checks an authentication-disabled host does not admit an anonymous stream.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test]
    public async Task NodeChangesHttp_AuthDisabledNeverBypassesKeyIdentity() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(authEnabled: false);
        using HttpClient client = fixture.Client();
        using HttpResponseMessage response = await client.GetAsync($"/api/v1/nodechanges?rootNodeId={fixture.RootId}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    /// <summary>Checks malformed filters and unsupported resume fields fail before ready.</summary>
    /// <param name="suffix">The invalid query suffix.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase("&unknown=x"), TestCase("&cursor=1"), TestCase("&resume=1"), TestCase("&id=0"), TestCase("&id=true"), TestCase("&events=16"), TestCase("&type=%20")]
    public async Task NodeChangesHttp_InvalidFilters_NoReady(string suffix) {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync();
        using HttpClient client = fixture.Client();
        using HttpResponseMessage response = await client.GetAsync($"/api/v1/nodechanges?rootNodeId={fixture.RootId}{suffix}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(response.Content.Headers.ContentType!.MediaType, Is.Not.EqualTo("text/event-stream"));
    }

    /// <summary>Checks replay headers and selector bounds cannot be silently ignored.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test]
    public async Task NodeChangesHttp_RejectsReplayAndSelectorOverflow() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync();
        using HttpClient client = fixture.Client();
        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/nodechanges?rootNodeId={fixture.RootId}");
        request.Headers.Add("Last-Event-ID", "1");
        using HttpResponseMessage replay = await client.SendAsync(request);
        Assert.That(replay.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        using HttpResponseMessage ids = await client.GetAsync($"/api/v1/nodechanges?rootNodeId={fixture.RootId}&id=" + string.Join(',', Enumerable.Range(1, 65)));
        Assert.That(ids.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        using HttpResponseMessage types = await client.GetAsync($"/api/v1/nodechanges?rootNodeId={fixture.RootId}&type=" + string.Join(',', Enumerable.Range(1, 17).Select(value => "t" + value)));
        Assert.That(types.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    /// <summary>Checks explicit IDs are direct readable members and combine with exact types by OR.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test]
    public async Task NodeChangesFilters_ExplicitIdsOrTypesAndDirectRootOnly() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync();
        long explicitId = await fixture.MemberAsync("anchor");
        await using NodeChangeSubscription sub = await Subscribe(fixture, new NodeChangeFilter { RootNodeId = fixture.RootId, Id = [explicitId, explicitId], Type = ["dynamic"] });
        await fixture.Nodes.Patch(explicitId, [Patch("/name", "changed")], fixture.OwnerId, false, default);
        Dirty(sub);
        await fixture.MemberAsync("other");
        Assert.That(sub.Reader.TryRead(out _), Is.False);
        await fixture.MemberAsync("dynamic");
        Dirty(sub);
        long indirect = (await fixture.Nodes.CreateNode(new NodeDetails { Name = "indirect", Type = "dynamic", Links = [fixture.RootId] }, fixture.OwnerId)).Id;
        Assert.That(sub.Reader.TryRead(out _), Is.False);
        Assert.ThrowsAsync<Pooshit.AspNetCore.Services.Errors.Exceptions.NotFoundException<Node>>(async () => {
            await using NodeChangeSubscription invalid = await Subscribe(fixture, new NodeChangeFilter { RootNodeId = fixture.RootId, Id = [indirect] });
        });
    }


    /// <summary>Checks current key and user authority changes close both idle and queued streams.</summary>
    /// <param name="scenario">The supported revocation producer.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase("key-enabled"), TestCase("key-permissions"), TestCase("key-owner"), TestCase("key-expiry"), TestCase("key-delete")]
    [TestCase("user-enabled"), TestCase("user-permissions"), TestCase("user-delete"), TestCase("root-owner"), TestCase("root-delete"), TestCase("explicit-id")]
    public async Task NodeChanges_IdleKeyUserRevocation_ClosesWithoutDiscoveryReads(string scenario) {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync();
        long member = await fixture.MemberAsync();
        await using NodeChangeSubscription sub = await Subscribe(fixture, new NodeChangeFilter { RootNodeId = fixture.RootId, Id = [member] });
        await fixture.Nodes.Patch(member, [Patch("/name", "queued")], fixture.OwnerId, false, default);
        Assert.That(sub.Reader.TryPeek(out _), Is.True);
        switch (scenario) {
            case "key-enabled": await fixture.Keys.UpdateApiKey(fixture.Key.Id, Patch("/enabled", false)); break;
            case "key-permissions": await fixture.Keys.UpdateApiKey(fixture.Key.Id, Patch("/permissions", Array.Empty<string>())); break;
            case "key-owner": await fixture.Keys.UpdateApiKey(fixture.Key.Id, Patch("/userId", await fixture.AddUserAsync())); break;
            case "key-expiry": await fixture.Keys.UpdateApiKey(fixture.Key.Id, Patch("/expiresAt", DateTime.UtcNow.AddHours(2))); break;
            case "key-delete": await fixture.Keys.DeleteApiKey(fixture.Key.Id); break;
            case "user-enabled": await fixture.Users.UpdateUser(fixture.OwnerId, Patch("/enabled", false)); break;
            case "user-permissions": await fixture.Users.UpdateUser(fixture.OwnerId, Patch("/permissions", new[] { "admin" })); break;
            case "user-delete": await fixture.Users.DeleteUser(fixture.OwnerId); break;
            case "root-owner": await fixture.Nodes.Patch(fixture.RootId, [Patch("/ownerId", await fixture.AddUserAsync())], fixture.OwnerId, true, default); break;
            case "root-delete": await fixture.Nodes.Delete(fixture.RootId, fixture.OwnerId, false); break;
            case "explicit-id": await fixture.Nodes.Delete(member, fixture.OwnerId, false); break;
        }
        Assert.That(sub.Closed, Is.True);
        Assert.That(await fixture.Feed.AdmitAsync(sub, default), Is.False);
    }

    /// <summary>Checks unreadable type membership stays private while visible entry and exit invalidate.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test]
    public async Task NodeChanges_RootAndNodeReadLoss_NoPostRevokeAdmission() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync();
        await fixture.Nodes.Patch(fixture.RootId, [Patch("/access", "Read")], fixture.OwnerId, false, default);
        long reader = await fixture.AddUserAsync();
        ApiKeyDetails key = await fixture.AddKeyAsync(reader, ["read"]);
        await using NodeChangeSubscription sub = await Subscribe(fixture, new NodeChangeFilter { RootNodeId = fixture.RootId, Type = ["configuration"], Events = NodeHookEvents.Status }, key, reader);
        long hidden = await fixture.MemberAsync();
        Assert.That(sub.Reader.TryRead(out _), Is.False);
        await fixture.Nodes.Patch(hidden, [Patch("/access", "Read")], fixture.OwnerId, false, default);
        Dirty(sub);
        await fixture.Nodes.Patch(hidden, [Patch("/access", "None")], fixture.OwnerId, false, default);
        Dirty(sub);
        await fixture.Nodes.Patch(hidden, [Patch("/status", "hidden-status-canary")], fixture.OwnerId, false, default);
        Assert.That(sub.Reader.TryRead(out _), Is.False);
        await fixture.Nodes.Patch(fixture.RootId, [Patch("/access", "None")], fixture.OwnerId, false, default);
        Assert.That(sub.Closed, Is.True);
        Assert.That(await fixture.Feed.AdmitAsync(sub, default), Is.False);
    }

    /// <summary>Checks a far future key lifetime remains valid and one short deadline closes without reads.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test]
    public async Task NodeChanges_ExpiryDeadlineSupportsAllLifetimesWithoutPolling() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync();
        await fixture.Keys.UpdateApiKey(fixture.Key.Id, Patch("/expiresAt", DateTime.MaxValue));
        await using (Connection distant = await Connect(fixture)) {
            await fixture.MemberAsync();
            Assert.That(await distant.Frame(), Is.EqualTo("event:invalidate\ndata:{}"));
        }
        await fixture.Keys.UpdateApiKey(fixture.Key.Id, Patch("/expiresAt", DateTime.UtcNow.AddSeconds(1)));
        await using NodeChangeSubscription sub = await Subscribe(fixture, new NodeChangeFilter { RootNodeId = fixture.RootId });
        int reads = fixture.Probe.Reads;
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        Assert.That(sub.Closed, Is.True);
        Assert.That(fixture.Probe.Reads, Is.EqualTo(reads));
        Assert.That(await fixture.Feed.AdmitAsync(sub, default), Is.False);
    }

    /// <summary>Checks capacity-one coalescing preserves changes after a writer takes its pending signal.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test]
    public async Task NodeChanges_DirtyMailboxCoalescesAndPreservesDuringWrite() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync();
        await using NodeChangeSubscription sub = await Subscribe(fixture, new NodeChangeFilter { RootNodeId = fixture.RootId });
        for (int index = 0; index < 25; ++index)
            await fixture.MemberAsync();
        Dirty(sub);
        Assert.That(sub.Reader.TryRead(out _), Is.False);
        Assert.That(await fixture.Feed.AdmitAsync(sub, default), Is.True);
        await fixture.MemberAsync();
        Dirty(sub);
        Assert.That(sub.Reader.TryRead(out _), Is.False);
    }

    /// <summary>Checks each connected socket has independent capacity and cancellation releases its slot.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test]
    public async Task NodeChanges_ConnectionCapacityCancellationAndIndependentOwners() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync();
        List<Connection> streams = [];
        try {
            for (int index = 0; index < 8; ++index)
                streams.Add(await Connect(fixture));
            using HttpClient ninth = fixture.Client();
            using HttpResponseMessage denied = await ninth.GetAsync($"/api/v1/nodechanges?rootNodeId={fixture.RootId}");
            Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            await fixture.MemberAsync();
            foreach (Connection stream in streams)
                Assert.That(await stream.Frame(), Is.EqualTo("event:invalidate\ndata:{}"));
            await streams[0].DisposeAsync();
            streams.RemoveAt(0);
            await using Connection replacement = await Connect(fixture);
        } finally {
            foreach (Connection stream in streams)
                await stream.DisposeAsync();
        }
    }

    /// <summary>Checks the global connection bound and shutdown release without a persisted subscription.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test]
    public async Task NodeChanges_GlobalCapacityAndShutdown() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync();
        List<NodeChangeSubscription> subscriptions = [];
        try {
            for (int keyIndex = 0; keyIndex < 16; ++keyIndex) {
                ApiKeyDetails key = await fixture.AddKeyAsync(fixture.OwnerId, ["read"]);
                for (int index = 0; index < 8; ++index)
                    subscriptions.Add(await Subscribe(fixture, new NodeChangeFilter { RootNodeId = fixture.RootId }, key));
            }
            ApiKeyDetails overflow = await fixture.AddKeyAsync(fixture.OwnerId, ["read"]);
            NodeChangeException? failure = Assert.ThrowsAsync<NodeChangeException>(async () => {
                await using NodeChangeSubscription unexpected = await Subscribe(fixture, new NodeChangeFilter { RootNodeId = fixture.RootId }, overflow);
            });
            Assert.That(failure!.Status, Is.EqualTo(HttpStatusCode.TooManyRequests));
            await fixture.Feed.StopAsync(default);
            Assert.That(subscriptions.All(sub => sub.Closed), Is.True);
        } finally {
            foreach (NodeChangeSubscription sub in subscriptions)
                await sub.DisposeAsync();
        }
    }

    /// <summary>Checks the current committed generation rejects stale positive and negative authority snapshots.</summary>
    /// <param name="postgres">Whether PostgreSQL is selected.</param>
    /// <param name="negative">Whether an initially denied key gains read authority.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase(false, false), TestCase(true, false), TestCase(false, true), TestCase(true, true)]
    public async Task NodeChanges_RegistrationGenerationFence_NoStaleGrantOrNegative(bool postgres, bool negative) {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(postgres);
        if (negative)
            await fixture.Keys.UpdateApiKey(fixture.Key.Id, Patch("/permissions", Array.Empty<string>()));
        TaskCompletionSource paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        fixture.Probe.BeforeRead = async sql => {
            bool match = sql.Contains(negative ? "divoid_user" : "node", StringComparison.OrdinalIgnoreCase);
            if (match && Interlocked.Exchange(ref entered, 1) == 0) {
                paused.SetResult();
                await release.Task;
            }
        };
        Task<NodeChangeSubscription> registering = Task.Run(() => Subscribe(fixture, new NodeChangeFilter { RootNodeId = fixture.RootId }));
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Keys.UpdateApiKey(fixture.Key.Id, Patch("/permissions", negative ? new[] { "read" } : Array.Empty<string>()));
        release.SetResult();
        if (negative) {
            await using NodeChangeSubscription accepted = await registering.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(accepted.Closed, Is.False);
        } else {
            Assert.ThrowsAsync<Backend.Errors.Exceptions.AuthorizationFailedException>(async () => await registering.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.That(entered, Is.EqualTo(1));
    }

    /// <summary>Checks four actual authority commits exhaust the fence without a registration leak.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test]
    public async Task NodeChanges_RegistrationGenerationConflicts_AreBounded() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync();
        int conflicts = 0;
        int inside = 0;
        fixture.Probe.BeforeRead = async sql => {
            if (sql.Contains("divoid_user", StringComparison.OrdinalIgnoreCase) && Volatile.Read(ref conflicts) < 4 && Interlocked.Exchange(ref inside, 1) == 0) {
                int conflict = Interlocked.Increment(ref conflicts);
                await fixture.Users.UpdateUser(fixture.OwnerId, Patch("/email", "change-" + conflict));
                Volatile.Write(ref inside, 0);
            }
        };
        NodeChangeException? failure = Assert.ThrowsAsync<NodeChangeException>(async () => {
            await using NodeChangeSubscription unexpected = await Subscribe(fixture, new NodeChangeFilter { RootNodeId = fixture.RootId });
        });
        Assert.That(failure!.Status, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(failure.Code, Is.EqualTo("graph_change_registration_busy"));
        Assert.That(conflicts, Is.EqualTo(4));
        fixture.Probe.BeforeRead = null;
        await using NodeChangeSubscription stable = await Subscribe(fixture, new NodeChangeFilter { RootNodeId = fixture.RootId });
        Assert.That(stable.Closed, Is.False);
    }

    /// <summary>Checks registration reads never hold the commit gate behind a prepared writer.</summary>
    /// <param name="postgres">Whether PostgreSQL is selected.</param>
    /// <param name="sharedCache">Whether SQLite uses shared cache.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase(false, false), TestCase(false, true), TestCase(true, false)]
    public async Task NodeChanges_SqliteRollbackJournalAndSharedCache_NoGateDbLockInversion(bool postgres, bool sharedCache) {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(postgres, sharedCache);
        long other = await fixture.AddUserAsync();
        Task<NodeChangeSubscription> registering;
        using (Transaction transaction = fixture.Database.Transaction()) {
            await fixture.Database.Update<Node>().Set(node => node.OwnerId == other).Where(node => node.Id == fixture.RootId).ExecuteAsync(transaction);
            registering = Task.Run(() => Subscribe(fixture, new NodeChangeFilter { RootNodeId = fixture.RootId }));
            await Task.Delay(50);
            await fixture.Feed.CommitAsync(transaction.Commit, [new NodeChangeDelta(
                new NodeChangeDelta.Fact(fixture.RootId, null, null, fixture.OwnerId, NodeAccess.None),
                new NodeChangeDelta.Fact(fixture.RootId, null, null, other, NodeAccess.None), NodeHookEvents.Metadata)]).WaitAsync(TimeSpan.FromSeconds(5));
        }
        try {
            await using NodeChangeSubscription acceptedBeforeCommit = await registering.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(acceptedBeforeCommit.Closed, Is.True);
        } catch (NodeChangeException exception) {
            Assert.That(exception.Status, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        } catch (Pooshit.AspNetCore.Services.Errors.Exceptions.NotFoundException<Node>) {
        }
    }

    /// <summary>Checks unknown commit outcomes and post-commit failures terminate continuity without replay.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test]
    public async Task NodeChanges_UnknownCommitAndPublicationFailure_ResetContinuity() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync();
        await using NodeChangeSubscription first = await Subscribe(fixture, new NodeChangeFilter { RootNodeId = fixture.RootId });
        int commits = 0;
        Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Feed.CommitAsync(() => { ++commits; throw new InvalidOperationException("unknown commit"); }, []));
        Assert.That(first.Closed, Is.True);
        Assert.That(first.Reader.TryRead(out _), Is.False);
        await using NodeChangeSubscription second = await Subscribe(fixture, new NodeChangeFilter { RootNodeId = fixture.RootId });
        Assert.ThrowsAsync<NullReferenceException>(() => fixture.Feed.CommitAsync(() => ++commits, [null!]));
        Assert.That(second.Closed, Is.True);
        Assert.That(commits, Is.EqualTo(2));
    }

    /// <summary>Checks connected keepalives perform no discovery and commits wake independently of the heartbeat.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test]
    public async Task NodeChanges_QuietConnectedConsumer_PerformsZeroDiscoveryReads() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync();
        await using Connection stream = await Connect(fixture);
        int idleReads = fixture.Probe.Reads;
        Assert.That(idleReads, Is.GreaterThan(0));
        Assert.That(await stream.Frame(20), Is.EqualTo(":keepalive"));
        Assert.That(fixture.Probe.Reads, Is.EqualTo(idleReads));
        await fixture.MemberAsync();
        int committedReads = fixture.Probe.Reads;
        Assert.That(committedReads, Is.GreaterThan(idleReads));
        Assert.That(await stream.Frame(), Is.EqualTo("event:invalidate\ndata:{}"));
        Assert.That(fixture.Probe.Reads, Is.EqualTo(committedReads));
    }


    /// <summary>Checks changes committed while ready is blocked remain pending behind the first frame.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test]
    public async Task NodeChanges_RegisterReadyBeforeSnapshot_NoLostRelevantCommit() {
        NodeChangeWriteBarrier barrier = new() { Armed = true };
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(configureServices: services => services.AddSingleton<IStartupFilter>(barrier));
        HttpClient client = fixture.Client();
        CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(10));
        Task<HttpResponseMessage> opening = client.GetAsync($"/api/v1/nodechanges?rootNodeId={fixture.RootId}", HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        long member = await fixture.MemberAsync();
        barrier.Armed = false;
        barrier.Release.SetResult();
        await using Connection stream = new(client, await opening, lifetime);
        Assert.That(await stream.Frame(), Does.StartWith("event:ready\n"));
        Assert.That(await stream.Frame(), Is.EqualTo("event:invalidate\ndata:{}"));
        NodeDetails state = await fixture.Nodes.GetNodeById(member, fixture.OwnerId, false);
        Assert.That(state.RootNodeId, Is.EqualTo(fixture.RootId));
        await fixture.Nodes.Patch(member, [Patch("/status", "during-snapshot")], fixture.OwnerId, false, default);
        Assert.That(await stream.Frame(), Is.EqualTo("event:invalidate\ndata:{}"));
    }

    /// <summary>Checks a real blocked writer neither holds the producer gate nor admits queued work after revocation.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test]
    public async Task NodeChanges_BlockedHttpWriter_RevocationAbortsWithoutBlockingCommit() {
        NodeChangeWriteBarrier barrier = new();
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(configureServices: services => services.AddSingleton<IStartupFilter>(barrier));
        await using Connection stream = await Connect(fixture);
        barrier.Armed = true;
        await fixture.MemberAsync();
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.MemberAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Keys.UpdateApiKey(fixture.Key.Id, Patch("/enabled", false)).WaitAsync(TimeSpan.FromSeconds(5));
        try {
            Assert.That(await stream.Frame(), Is.EqualTo("<eof>"));
        } catch (IOException) {
        }
    }

    /// <summary>Checks a stalled network writer is bounded even without another producer event.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test]
    public async Task NodeChanges_SlowHttpWriter_DisconnectsAtItsBound() {
        NodeChangeWriteBarrier barrier = new();
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(configureServices: services => services.AddSingleton<IStartupFilter>(barrier));
        await using Connection stream = await Connect(fixture);
        barrier.Armed = true;
        await fixture.MemberAsync();
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.ThrowsAsync<IOException>(async () => await stream.Frame(20));
    }

    /// <summary>Checks a hint insertion failure rolls back graph data and emits no invalidation.</summary>
    /// <param name="postgres">Whether PostgreSQL is selected.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase(false), TestCase(true)]
    public async Task NodeChanges_Rollback_DiscardsAttemptDeltas(bool postgres) {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(postgres);
        long member = await fixture.MemberAsync();
        await fixture.Nodes.RegisterHook(new NodeHook { NodeId = member, UserId = fixture.OwnerId, Tag = "rollback" }, fixture.OwnerId, false);
        await using NodeChangeSubscription sub = await Subscribe(fixture, new NodeChangeFilter { RootNodeId = fixture.RootId });
        if (postgres) {
            await fixture.Database.DBClient.NonQueryAsync("CREATE FUNCTION reject_feed_hint() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic hint failure'; END $$");
            await fixture.Database.DBClient.NonQueryAsync("CREATE TRIGGER reject_feed_hint BEFORE INSERT ON message FOR EACH ROW EXECUTE FUNCTION reject_feed_hint()");
        } else
            await fixture.Database.DBClient.NonQueryAsync("CREATE TRIGGER reject_feed_hint BEFORE INSERT ON message BEGIN SELECT RAISE(ABORT, 'synthetic hint failure'); END");
        Assert.CatchAsync(() => fixture.Nodes.Patch(member, [Patch("/status", "rejected")], fixture.OwnerId, false, default));
        Assert.That((await fixture.Nodes.GetNodeById(member, fixture.OwnerId, false)).Status, Is.Null);
        Assert.That(await fixture.Database.Load<Message>(DB.Count()).ExecuteScalarAsync<long>(), Is.Zero);
        Assert.That(sub.Reader.TryRead(out _), Is.False);
        Assert.That(sub.Closed, Is.False);
    }

    /// <summary>Checks PostgreSQL action and commit serialization aborts retry a fresh unit and publish once.</summary>
    /// <param name="atCommit">Whether the abort occurs during commit.</param>
    /// <param name="failures">The injected serialization failures.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase(false, 2), TestCase(true, 2), TestCase(false, 100), TestCase(true, 100)]
    public async Task NodeChanges_SerializationRetry_PublishConfirmedCommitOnly(bool atCommit, int failures) {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(true);
        long member = await fixture.MemberAsync();
        await using NodeChangeSubscription sub = await Subscribe(fixture, new NodeChangeFilter { RootNodeId = fixture.RootId });
        await fixture.Database.DBClient.NonQueryAsync("CREATE SEQUENCE feed_attempts");
        await fixture.Database.DBClient.NonQueryAsync("CREATE FUNCTION abort_feed_attempt() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF nextval('feed_attempts') <= " + failures + " THEN RAISE EXCEPTION USING ERRCODE = '40001', MESSAGE = 'synthetic serialization abort'; END IF; RETURN NEW; END $$");
        await fixture.Database.DBClient.NonQueryAsync(atCommit
            ? "CREATE CONSTRAINT TRIGGER abort_feed_attempt AFTER UPDATE ON node DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status) EXECUTE FUNCTION abort_feed_attempt()"
            : "CREATE TRIGGER abort_feed_attempt BEFORE UPDATE OF status ON node FOR EACH ROW EXECUTE FUNCTION abort_feed_attempt()");
        if (failures < 8) {
            await fixture.Nodes.Patch(member, [Patch("/status", "committed")], fixture.OwnerId, false, default);
            Dirty(sub);
            Assert.That(sub.Reader.TryRead(out _), Is.False);
        } else {
            Assert.CatchAsync(() => fixture.Nodes.Patch(member, [Patch("/status", "rejected")], fixture.OwnerId, false, default));
            Assert.That(sub.Reader.TryRead(out _), Is.False);
            Assert.That((await fixture.Nodes.GetNodeById(member, fixture.OwnerId, false)).Status, Is.Null);
        }
        Assert.That(fixture.PostgresScalar<long>("SELECT last_value FROM feed_attempts"), Is.EqualTo(failures < 8 ? failures + 1 : 8));
    }

    /// <summary>Checks authority preparation does not acquire the gate while waiting for a rollback-journal writer.</summary>
    /// <param name="key">Whether key or user authority is changed.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase(false), TestCase(true)]
    public async Task NodeChanges_AuthorityPreparationOutsideGate_NoWriterInversion(bool key) {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync();
        await using NodeChangeSubscription sub = await Subscribe(fixture, new NodeChangeFilter { RootNodeId = fixture.RootId });
        Task authority;
        using (Transaction transaction = fixture.Database.Transaction()) {
            await fixture.Database.Update<Node>().Set(node => node.X == 7d).Where(node => node.Id == fixture.RootId).ExecuteAsync(transaction);
            TaskCompletionSource preparing = new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Probe.BeforeTransaction = () => preparing.TrySetResult();
            authority = Task.Run(() => key
                ? fixture.Keys.UpdateApiKey(fixture.Key.Id, Patch("/enabled", false))
                : fixture.Users.UpdateUser(fixture.OwnerId, Patch("/enabled", false)));
            await preparing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task committing = fixture.Feed.CommitAsync(transaction.Commit, []);
            Assert.That(await Task.WhenAny(committing, authority), Is.SameAs(committing));
            await committing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await authority.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(sub.Closed, Is.True);
    }

    /// <summary>Checks unrelated principals and last-used maintenance do not cancel a live connection.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test]
    public async Task NodeChanges_UnrelatedAuthorityAndLastUsed_DoNotClose() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync();
        await using NodeChangeSubscription sub = await Subscribe(fixture, new NodeChangeFilter { RootNodeId = fixture.RootId });
        await fixture.Keys.GetApiKey(fixture.Key.PlaintextKey);
        await fixture.Keys.UpdateApiKey(fixture.Key.Id, Patch("/lastUsedAt", DateTime.UtcNow));
        long other = await fixture.AddUserAsync();
        ApiKeyDetails key = await fixture.AddKeyAsync(other, ["read"]);
        await fixture.Keys.UpdateApiKey(key.Id, Patch("/enabled", false));
        await fixture.Users.UpdateUser(other, Patch("/enabled", false));
        Assert.That(sub.Closed, Is.False);
        Assert.That(await fixture.Feed.AdmitAsync(sub, default), Is.True);
    }

    /// <summary>Checks independent processes are explicitly outside the live singleton's coverage.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test]
    public async Task NodeChanges_ProcessCoverageIsHonest() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync();
        await using NodeChangeSubscription sub = await Subscribe(fixture, new NodeChangeFilter { RootNodeId = fixture.RootId });
        NodeChangeFeed external = new();
        NodeService externalWriter = new(fixture.Database, new EmbeddingCapability(false), EmbeddingProducers.Function, external);
        long member = (await externalWriter.CreateNode(new NodeDetails { Name = "external", RootNodeId = fixture.RootId }, fixture.OwnerId)).Id;
        Assert.That(member, Is.GreaterThan(0));
        Assert.That(sub.Reader.TryRead(out _), Is.False);
    }


    /// <summary>Checks time expiry between fresh reads and final registration cannot admit stale authority.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test]
    public async Task NodeChanges_ExpiryBeforeGenerationCompare_NoReady() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync();
        await fixture.Keys.UpdateApiKey(fixture.Key.Id, Patch("/expiresAt", DateTime.UtcNow.AddSeconds(1)));
        TaskCompletionSource paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int once = 0;
        fixture.Probe.BeforeRead = async sql => {
            if (sql.Contains("node", StringComparison.OrdinalIgnoreCase) && Interlocked.Exchange(ref once, 1) == 0) {
                paused.SetResult();
                await release.Task;
            }
        };
        Task<NodeChangeSubscription> registering = Task.Run(() => Subscribe(fixture, new NodeChangeFilter { RootNodeId = fixture.RootId }));
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        release.SetResult();
        Assert.ThrowsAsync<AuthenticationFailedException>(async () => await registering.WaitAsync(TimeSpan.FromSeconds(5)));
        fixture.Probe.BeforeRead = null;
    }

    /// <summary>Checks a reconnect always requests resynchronization and then sees current state.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test]
    public async Task NodeChanges_DisconnectReconnectRequiresFullResync() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync();
        await using (Connection initial = await Connect(fixture)) {
            await fixture.MemberAsync();
            Assert.That(await initial.Frame(), Is.EqualTo("event:invalidate\ndata:{}"));
        }
        long duringGap = await fixture.MemberAsync();
        await using Connection reconnected = await Connect(fixture);
        Assert.That((await fixture.Nodes.GetNodeById(duringGap, fixture.OwnerId, false)).RootNodeId, Is.EqualTo(fixture.RootId));
        await fixture.Nodes.Patch(duringGap, [Patch("/status", "after-reconnect")], fixture.OwnerId, false, default);
        Assert.That(await reconnected.Frame(), Is.EqualTo("event:invalidate\ndata:{}"));
    }


    /// <summary>Checks physical timer segments preserve one far-future deadline and cannot rearm after revocation.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test]
    public async Task NodeChanges_LogicalDeadlineSegment_StopsAfterRevocationAndDispose() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync();
        await fixture.Keys.UpdateApiKey(fixture.Key.Id, Patch("/expiresAt", DateTime.MaxValue));
        NodeChangeSubscription sub = await Subscribe(fixture, new NodeChangeFilter { RootNodeId = fixture.RootId });
        int reads = fixture.Probe.Reads;
        System.Reflection.MethodInfo segment = typeof(NodeChangeSubscription).GetMethod("Expire", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        segment.Invoke(sub, null);
        Assert.That(sub.ExpiresAt, Is.EqualTo(DateTime.MaxValue));
        Assert.That(sub.Closed, Is.False);
        Assert.That(fixture.Probe.Reads, Is.EqualTo(reads));
        await fixture.Keys.UpdateApiKey(fixture.Key.Id, Patch("/enabled", false));
        Assert.That(sub.Closed, Is.True);
        await sub.DisposeAsync();
        Assert.DoesNotThrow(() => segment.Invoke(sub, null));
    }

    static PatchOperation Patch(string path, object value) => new() { Op = "replace", Path = path, Value = value };

    static Task<NodeChangeSubscription> Subscribe(NodeChangeFeedFixture fixture, NodeChangeFilter filter, ApiKeyDetails? key = null, long? user = null) =>
        fixture.Feed.SubscribeAsync(fixture.Database, (key ?? fixture.Key).Id, user ?? fixture.OwnerId, filter, default);

    static void Dirty(NodeChangeSubscription subscription) => Assert.That(subscription.Reader.TryRead(out bool dirty) && dirty, Is.True);

    static async Task<Connection> Connect(NodeChangeFeedFixture fixture, string suffix = "", ApiKeyDetails? key = null) {
        HttpClient client = fixture.Client(key);
        CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(60));
        HttpResponseMessage response = await client.GetAsync($"/api/v1/nodechanges?rootNodeId={fixture.RootId}{suffix}", HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Connection connection = new(client, response, cancellation);
        Assert.That(await connection.Frame(), Is.EqualTo("event:ready\ndata:{\"version\":1,\"coverage\":\"this-process\",\"replay\":false,\"resyncRequired\":true}"));
        return connection;
    }

    sealed class Connection(HttpClient client, HttpResponseMessage response, CancellationTokenSource cancellation) : IAsyncDisposable {
        readonly StreamReader reader = new(response.Content.ReadAsStream());
        internal HttpResponseMessage Response => response;

        internal async Task<string> Frame(int seconds = 5) {
            List<string> lines = [];
            while (true) {
                string? line = await reader.ReadLineAsync(cancellation.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(seconds));
                if (line == null)
                    return "<eof>";
                if (line.Length == 0)
                    return string.Join('\n', lines);
                lines.Add(line);
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync() {
            await cancellation.CancelAsync();
            reader.Dispose();
            response.Dispose();
            client.Dispose();
            cancellation.Dispose();
        }
    }

}
