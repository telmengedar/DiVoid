using System.Net;
using Microsoft.AspNetCore.Hosting;
using Npgsql;
using Backend.Models.Auth;
using Backend.Models.Users;
using Backend.Models.Nodes;
using Backend.Services.Auth;
using Backend.Services.Messages;
using Backend.Services.MessageChanges;
using Microsoft.Extensions.DependencyInjection;
using Pooshit.Http;
using Pooshit.Json;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Tokens;
using Backend.Models.Messages;
using Backend.tests.Fixtures;
using Pooshit.Http.Paths;

namespace Backend.tests.Tests;

/// <summary>Exercises committed recipient inbox changes through actual authenticated HTTP and PostgreSQL.</summary>
[TestFixture, Parallelizable]
public class MessageChangeFeedTests {
    /// <summary>Checks a committed ordinary message wakes its recipient's real open stream.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test, Parallelizable]
    public async Task InboxChanges_MessageCommit_WakesRealRecipient() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(postgres: true);
        await using NodeChangeFeedConnection stream = await NodeChangeFeedConnection.OpenAsync(fixture.Host,
            Rest.Path(TestSetup.BaseUrl, "api", "v1", "messagechanges"), fixture.Key.PlaintextKey);
        Assert.That(await stream.Frame(), Is.EqualTo("event:ready\ndata:{\"version\":1,\"coverage\":\"postgres-database\",\"replay\":false,\"resyncRequired\":true}"));
        using HttpResponseMessage created = await fixture.HttpService().Post<MessageDetails, HttpResponseMessage>(
            Rest.Path(TestSetup.BaseUrl, "api", "messages"), new MessageDetails { RecipientId = fixture.OwnerId, Subject = "[test] inbox", Body = "private canary" }, fixture.Options());
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await stream.Frame(), Is.EqualTo("event:invalidate\ndata:{}"));
    }

    /// <summary>Checks transactional rollback never wakes early and a later commit remains observable.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test, Parallelizable]
    public async Task InboxChanges_RollbackAndBootstrap_NoPhantomOrLostCommit() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(postgres: true);
        await fixture.Host.Services.GetRequiredService<MessageService>().Create(fixture.OwnerId,
            new MessageDetails { RecipientId = fixture.OwnerId, Subject = "before listen", Body = "durable" });
        await using NodeChangeFeedConnection stream = await Open(fixture);
        Assert.That(await Count(fixture), Is.EqualTo(1));
        Task<string> pending = stream.Frame();
        using (Transaction transaction = fixture.Database.Transaction()) {
            long id = await fixture.Database.Insert<Message>().Columns(row => row.AuthorId, row => row.RecipientId, row => row.Subject, row => row.Body, row => row.CreatedAt)
                .Values(0L, fixture.OwnerId, "rolled back", "not visible", DateTime.UtcNow).ReturnID().ExecuteAsync(transaction);
            Assert.That(id, Is.GreaterThan(0));
            Assert.That(await Count(fixture), Is.EqualTo(1));
            await Task.Delay(150);
            Assert.That(pending.IsCompleted, Is.False);
            transaction.Rollback();
        }
        await fixture.Host.Services.GetRequiredService<MessageService>().Create(fixture.OwnerId,
            new MessageDetails { RecipientId = fixture.OwnerId, Subject = "committed", Body = "visible" });
        Assert.That(await pending, Is.EqualTo("event:invalidate\ndata:{}"));
        Assert.That(await Count(fixture), Is.EqualTo(2));
    }

    /// <summary>Checks a sender's outbox cannot wake another recipient's stream and deletion does wake it.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test, Parallelizable]
    public async Task InboxChanges_OwnRecipientAndDelete_AllTagsNoOutboxLeak() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(postgres: true);
        long other = await fixture.AddUserAsync();
        await using NodeChangeFeedConnection stream = await Open(fixture);
        Task<string> pending = stream.Frame();
        await fixture.Host.Services.GetRequiredService<MessageService>().Create(fixture.OwnerId,
            new MessageDetails { RecipientId = other, Subject = "[other] private", Body = "not this inbox" });
        await Task.Delay(150);
        Assert.That(pending.IsCompleted, Is.False);
        MessageDetails received = await fixture.Host.Services.GetRequiredService<MessageService>().Create(other,
            new MessageDetails { RecipientId = fixture.OwnerId, Subject = "[any-tag] own", Body = "private" });
        Assert.That(await pending, Is.EqualTo("event:invalidate\ndata:{}"));
        await fixture.Host.Services.GetRequiredService<MessageService>().Delete(fixture.OwnerId, false, received.Id);
        Assert.That(await stream.Frame(), Is.EqualTo("event:invalidate\ndata:{}"));
    }

    /// <summary>Checks direct node-hook inserts join the same database producer without app callbacks.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test, Parallelizable]
    public async Task InboxChanges_DirectNodeHookProducer_WakesRecipient() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(postgres: true);
        long node = await fixture.MemberAsync();
        await fixture.Nodes.RegisterHook(new NodeHook { NodeId = node, UserId = fixture.OwnerId, Tag = "private-tag" }, fixture.OwnerId, false);
        await using NodeChangeFeedConnection stream = await Open(fixture);
        await fixture.Nodes.Patch(node, [new Pooshit.AspNetCore.Services.Patches.PatchOperation { Op = "replace", Path = "/status", Value = "done" }], fixture.OwnerId, false, default);
        Assert.That(await stream.Frame(), Is.EqualTo("event:invalidate\ndata:{}"));
        Assert.That(await Count(fixture), Is.EqualTo(1));
    }

    /// <summary>Checks native Teams commit wakes once while dedup replay cannot replace the durable row.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test, Parallelizable]
    public async Task InboxChanges_TeamsOuterCommitAndDedup_WakeRecipient() {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(true);
        await Install(database.Entities);
        using TeamsRoutingFixture fixture = new(database);
        ApiKeyDetails key = await fixture.Host.Services.GetRequiredService<IApiKeyService>().CreateApiKey(new ApiKeyParameters { UserId = database.RecipientId, Permissions = ["read"] });
        await using NodeChangeFeedConnection stream = await NodeChangeFeedConnection.OpenAsync(fixture.Host.CreateHandler(), Path(), key.PlaintextKey);
        Assert.That(await stream.Frame(), Does.Contain("postgres-database"));
        using HttpResponseMessage ingress = await fixture.Host.Http.Post<Dictionary<string, object?>, HttpResponseMessage>(Rest.Path(TestSetup.BaseUrl, "api", "teams", "messages"), TeamsWebhookFixture.Activity(), Options(fixture.Host.Token()));
        Assert.That(ingress.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await stream.Frame(), Is.EqualTo("event:invalidate\ndata:{}"));
        using HttpResponseMessage replay = await fixture.Host.Http.Post<Dictionary<string, object?>, HttpResponseMessage>(Rest.Path(TestSetup.BaseUrl, "api", "teams", "messages"), TeamsWebhookFixture.Activity(), Options(fixture.Host.Token()));
        Assert.That(replay.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await database.Entities.Load<Message>(DB.Count()).ExecuteScalarAsync<long>(), Is.EqualTo(1));
        Task<string> pending = stream.Frame();
        await Task.Delay(150);
        Assert.That(pending.IsCompleted, Is.False);
        await fixture.Host.Services.GetRequiredService<MessageService>().Delete(database.RecipientId, false,
            (await database.Entities.Load<Message>().ExecuteEntityAsync()).Id);
        Assert.That(await pending, Is.EqualTo("event:invalidate\ndata:{}"));
        Assert.That(fixture.Host.Connector.Bodies, Is.Empty);
    }

    /// <summary>Checks cross-process authority writes close idle streams without another inbox operation.</summary>
    /// <param name="change">The independent database authority mutation.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase("key-disable"), TestCase("key-delete"), TestCase("key-owner"), TestCase("key-permissions"), TestCase("key-hash")]
    [TestCase("user-disable"), TestCase("user-delete"), TestCase("user-permissions"), TestCase("key-expiry"), Parallelizable]
    public async Task InboxChanges_AuthorityDatabaseWrites_CloseIdle(string change) {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(postgres: true);
        await using NodeChangeFeedConnection stream = await Open(fixture);
        switch (change) {
            case "key-disable": await fixture.Database.Update<ApiKey>().Set(row => row.Enabled == false).Where(row => row.Id == fixture.Key.Id).ExecuteAsync(); break;
            case "key-delete": await fixture.Database.Delete<ApiKey>().Where(row => row.Id == fixture.Key.Id).ExecuteAsync(); break;
            case "key-owner": await fixture.Database.Update<ApiKey>().Set(row => row.UserId == 999).Where(row => row.Id == fixture.Key.Id).ExecuteAsync(); break;
            case "key-permissions": await fixture.Database.Update<ApiKey>().Set(row => row.Permissions == "[]").Where(row => row.Id == fixture.Key.Id).ExecuteAsync(); break;
            case "key-hash":
                byte[] changedHash = new byte[32];
                await fixture.Database.Update<ApiKey>().Set(row => row.KeyHash == changedHash).Where(row => row.Id == fixture.Key.Id).ExecuteAsync();
                break;
            case "key-expiry": await fixture.Database.Update<ApiKey>().Set(row => row.ExpiresAt == DateTime.UtcNow.AddHours(1)).Where(row => row.Id == fixture.Key.Id).ExecuteAsync(); break;
            case "user-disable": await fixture.Database.Update<User>().Set(row => row.Enabled == false).Where(row => row.Id == fixture.OwnerId).ExecuteAsync(); break;
            case "user-delete": await fixture.Database.Delete<User>().Where(row => row.Id == fixture.OwnerId).ExecuteAsync(); break;
            case "user-permissions": await fixture.Database.Update<User>().Set(row => row.Permissions == "[\"admin\"]").Where(row => row.Id == fixture.OwnerId).ExecuteAsync(); break;
        }
        Assert.That(await stream.Frame(), Is.EqualTo("<eof>"));
    }

    /// <summary>Checks idle keepalives perform no authority or inbox reads and LastUsedAt does not revoke.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test, Parallelizable]
    public async Task InboxChanges_QuietConnected_NoBusinessDiscoveryQueries() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(postgres: true);
        await using NodeChangeFeedConnection stream = await Open(fixture);
        int reads = fixture.Probe.Reads;
        Assert.That(reads, Is.GreaterThan(0));
        Assert.That(await stream.Frame(20), Is.EqualTo(":keepalive"));
        Assert.That(fixture.Probe.Reads, Is.EqualTo(reads));
        await fixture.Database.Update<ApiKey>().Set(row => row.LastUsedAt == DateTime.UtcNow).Where(row => row.Id == fixture.Key.Id).ExecuteAsync();
        await fixture.Host.Services.GetRequiredService<MessageService>().Create(fixture.OwnerId, new MessageDetails { RecipientId = fixture.OwnerId, Subject = "after quiet", Body = "private" });
        Assert.That(await stream.Frame(), Is.EqualTo("event:invalidate\ndata:{}"));
    }

    /// <summary>Checks the logical expiry deadline closes an idle connection.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test, Parallelizable]
    public async Task InboxChanges_Expiry_ClosesIdle() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(postgres: true);
        DateTime expiry = DateTime.SpecifyKind(DateTime.UtcNow.AddSeconds(2), DateTimeKind.Unspecified);
        Assert.That(fixture.PostgresScalar<long>("UPDATE apikey SET expiresat=@expiry WHERE id=@id RETURNING id", ("expiry", expiry), ("id", fixture.Key.Id)), Is.EqualTo(fixture.Key.Id));
        ApiKey stored = await fixture.Database.Load<ApiKey>().Where(row => row.Id == fixture.Key.Id).ExecuteEntityAsync();
        Assert.That(stored.ExpiresAt, Is.EqualTo(expiry).Within(TimeSpan.FromMilliseconds(1)));
        await using NodeChangeFeedConnection stream = await Open(fixture);
        Assert.That(await stream.Frame(), Is.EqualTo("<eof>"));
    }

    /// <summary>Checks provider and authentication boundaries fail safely before ready.</summary>
    /// <param name="scenario">The rejected request.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase("sqlite"), TestCase("jwt"), TestCase("query"), TestCase("cursor"), Parallelizable]
    public async Task InboxChanges_RejectedRequest_NoReady(string scenario) {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(postgres: scenario != "sqlite");
        if (scenario == "jwt")
            await fixture.Database.Update<User>().Set(row => row.Permissions == "[\"read\"]").Where(row => row.Id == fixture.OwnerId).ExecuteAsync();
        string path = scenario == "query" ? Rest.PathQuery(new QueryParameters("recipientId", 999), TestSetup.BaseUrl, "api", "v1", "messagechanges") : Path();
        string token = scenario == "jwt" ? fixture.Host.MintToken(userId: fixture.OwnerId) : fixture.Key.PlaintextKey;
        using HttpRequestMessage request = await fixture.HttpService().CreateRequest(path, HttpMethod.Get, Options(token));
        if (scenario == "cursor")
            request.Headers.Add("Last-Event-ID", "unsupported");
        using HttpResponseMessage response = await fixture.HttpService().Send<HttpResponseMessage>(request, null);
        Assert.That(response.StatusCode, Is.EqualTo(scenario switch { "sqlite" => HttpStatusCode.ServiceUnavailable, "jwt" => HttpStatusCode.Unauthorized, _ => HttpStatusCode.BadRequest }));
        Assert.That(response.Content.Headers.ContentType!.MediaType, Is.Not.EqualTo("text/event-stream"));
        if (scenario == "sqlite")
            Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("inbox_changes_provider_unsupported"));
    }

    /// <summary>Checks per-key capacity and cancellation release the actual dedicated-session slot.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test, Parallelizable]
    public async Task InboxChanges_CapacityAndCancellation_ReleasesSlot() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(postgres: true);
        await using NodeChangeFeedConnection first = await Open(fixture);
        NodeChangeFeedConnection second = await Open(fixture);
        using HttpResponseMessage denied = await fixture.HttpService().Get<HttpResponseMessage>(Path(), fixture.Options());
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        await second.DisposeAsync();
        await using NodeChangeFeedConnection replacement = await Open(fixture);
        await fixture.Host.Services.GetRequiredService<MessageService>().Create(fixture.OwnerId, new MessageDetails { RecipientId = fixture.OwnerId, Subject = "new slot", Body = "private" });
        Assert.That(await replacement.Frame(), Is.EqualTo("event:invalidate\ndata:{}"));
    }

    /// <summary>Checks owner privileges, serialized idempotency, native LISTEN and denied DDL rollback.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test, Parallelizable]
    public async Task InboxChanges_ProviderBootstrap_NonsuperuserOwnerAndDeniedRole() {
        await using InboxPostgresOwnerFixture fixture = new();
        await fixture.StartAsync();
        await Task.WhenAll(Install(fixture.Entities), Install(fixture.Entities));
        await using NpgsqlConnection listener = await MessageChangeSchema.OpenAsync(fixture.Entities, default);
        await using (NpgsqlCommand role = new("SELECT rolsuper FROM pg_roles WHERE rolname=current_user", listener))
            Assert.That(await role.ExecuteScalarAsync(), Is.False);
        MessageChangeTable table = await MessageChangeSchema.ResolveAsync<Message>(fixture.Entities, listener);
        string channel = MessageChangeSchema.Channel("message", table.Oid, 1);
        await using (NpgsqlCommand listen = new("LISTEN " + MessageChangeSchema.Quote(channel), listener))
            await listen.ExecuteNonQueryAsync();
        string? payload = null;
        listener.Notification += (_, notification) => payload = notification.Payload;
        long id = await fixture.Entities.Insert<Message>().Columns(row => row.AuthorId, row => row.RecipientId, row => row.Subject, row => row.Body, row => row.CreatedAt)
            .Values(0L, 1L, "owner insert", "private body", DateTime.UtcNow).ReturnID().ExecuteAsync();
        Assert.That(id, Is.GreaterThan(0));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await listener.WaitAsync(timeout.Token);
        Assert.That(payload, Is.EqualTo(""));
        PostgresException? denied = Assert.ThrowsAsync<PostgresException>(() => Install(fixture.DeniedEntities));
        Assert.That(denied!.SqlState, Is.EqualTo(PostgresErrorCodes.InsufficientPrivilege));
        await MessageChangeSchema.RequireInstalledAsync(listener, [table]);
    }

    /// <summary>Checks fixed per-process capacity independently from the per-key bound.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test, Parallelizable]
    public async Task InboxChanges_GlobalCapacity_UsesAtMostEightSessions() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(postgres: true);
        List<NodeChangeFeedConnection> streams = [];
        try {
            for (int index = 0; index < 4; ++index) {
                ApiKeyDetails key = await fixture.AddKeyAsync(fixture.OwnerId, ["read"]);
                streams.Add(await Open(fixture, key));
                streams.Add(await Open(fixture, key));
            }
            ApiKeyDetails ninth = await fixture.AddKeyAsync(fixture.OwnerId, ["read"]);
            using HttpResponseMessage denied = await fixture.HttpService().Get<HttpResponseMessage>(Path(), fixture.Options(ninth));
            Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(Sessions(fixture), Is.EqualTo(8));
        } finally {
            foreach (NodeChangeFeedConnection stream in streams)
                await stream.DisposeAsync();
        }
        await using NodeChangeFeedConnection replacement = await Open(fixture);
    }

    /// <summary>Checks native listener failure closes continuity without a hidden reconnect.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test, Parallelizable]
    public async Task InboxChanges_ListenerFailure_RequiresFreshReady() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(postgres: true);
        await using (NodeChangeFeedConnection stream = await Open(fixture)) {
            Assert.That(Sessions(fixture), Is.EqualTo(1));
            Assert.That(fixture.PostgresScalar<bool>("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname=current_database() AND application_name='divoid-inbox-events'"), Is.True);
            Assert.That(await stream.Frame(), Is.EqualTo("<eof>"));
        }
        Assert.That(Sessions(fixture), Is.Zero);
        await fixture.Host.Services.GetRequiredService<MessageService>().Create(fixture.OwnerId, new MessageDetails { RecipientId = fixture.OwnerId, Subject = "gap", Body = "durable" });
        await using NodeChangeFeedConnection fresh = await Open(fixture);
        Assert.That(await Count(fixture), Is.EqualTo(1));
    }

    /// <summary>Checks a blocked network write cannot block a commit or database-triggered revocation.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test, Parallelizable]
    public async Task InboxChanges_BlockedWriter_DoesNotBlockCommitOrRevocation() {
        NodeChangeWriteBarrier barrier = new();
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(postgres: true, configureServices: services => services.AddSingleton<IStartupFilter>(barrier));
        await using NodeChangeFeedConnection stream = await Open(fixture);
        barrier.Armed = true;
        MessageService messages = fixture.Host.Services.GetRequiredService<MessageService>();
        await messages.Create(fixture.OwnerId, new MessageDetails { RecipientId = fixture.OwnerId, Subject = "blocked", Body = "private" });
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await messages.Create(fixture.OwnerId, new MessageDetails { RecipientId = fixture.OwnerId, Subject = "not blocked", Body = "private" }).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(await Count(fixture), Is.EqualTo(2));
        await fixture.Database.Update<ApiKey>().Set(row => row.Enabled == false).Where(row => row.Id == fixture.Key.Id).ExecuteAsync();
        Assert.That(await stream.Frame(), Is.EqualTo("<eof>"));
    }

    /// <summary>Checks a truly stalled socket writer ends at the existing fixed transport bound.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test, Parallelizable]
    public async Task InboxChanges_SlowWriter_DisconnectsAtBound() {
        NodeChangeWriteBarrier barrier = new();
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(postgres: true, configureServices: services => services.AddSingleton<IStartupFilter>(barrier));
        await using NodeChangeFeedConnection stream = await Open(fixture);
        barrier.Armed = true;
        await fixture.Host.Services.GetRequiredService<MessageService>().Create(fixture.OwnerId, new MessageDetails { RecipientId = fixture.OwnerId, Subject = "stalled", Body = "private" });
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try {
            Assert.That(await stream.Frame(20), Is.EqualTo("<eof>"));
        } catch (IOException) {
        }
    }

    /// <summary>Checks distinct schema/table identities cannot cross-wake the same numeric recipient.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test, Parallelizable]
    public async Task InboxChanges_MultiSchemaTableOid_IsolatesChannels() {
        await using InboxPostgresOwnerFixture first = new();
        await using InboxPostgresOwnerFixture second = new();
        await first.StartAsync();
        await second.StartAsync(first.Schema);
        await using NpgsqlConnection one = await MessageChangeSchema.OpenAsync(first.Entities, default);
        await using NpgsqlConnection two = await MessageChangeSchema.OpenAsync(second.Entities, default);
        MessageChangeTable a = await MessageChangeSchema.ResolveAsync<Message>(first.Entities, one);
        MessageChangeTable b = await MessageChangeSchema.ResolveAsync<Message>(second.Entities, two);
        string channelA = MessageChangeSchema.Channel("message", a.Oid, 1);
        string channelB = MessageChangeSchema.Channel("message", b.Oid, 1);
        Assert.That(a.Oid, Is.Not.EqualTo(b.Oid));
        Assert.That(channelA, Is.Not.EqualTo(channelB));
        await using (NpgsqlCommand listen = new("LISTEN " + MessageChangeSchema.Quote(channelA), one))
            await listen.ExecuteNonQueryAsync();
        await using (NpgsqlCommand listen = new("LISTEN " + MessageChangeSchema.Quote(channelB), two))
            await listen.ExecuteNonQueryAsync();
        List<string> observed = [];
        one.Notification += (_, notification) => {
            Assert.That(notification.Payload, Is.Empty);
            observed.Add(notification.Channel);
        };
        await Insert(second.Entities, 1);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await two.WaitAsync(timeout.Token);
        await Insert(first.Entities, 1);
        await one.WaitAsync(timeout.Token);
        Assert.That(observed, Is.EqualTo(new[] { channelA }));
    }

    /// <summary>Checks real deferred serialization aborts discard message notifications and only commit wakes.</summary>
    /// <param name="exhausted">Whether all existing retry attempts are deliberately rejected.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task InboxChanges_NativeSerializationRetry_PublishesCommittedAttemptOnly(bool exhausted) {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(postgres: true);
        long member = await fixture.MemberAsync();
        await fixture.Nodes.RegisterHook(new NodeHook { NodeId = member, UserId = fixture.OwnerId, Tag = "retry" }, fixture.OwnerId, false);
        await using NodeChangeFeedConnection stream = await Open(fixture);
        await fixture.Database.DBClient.NonQueryAsync("CREATE SEQUENCE inbox_attempts");
        string failures = exhausted ? "100" : "2";
        await fixture.Database.DBClient.NonQueryAsync("CREATE FUNCTION reject_inbox_attempt() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF nextval('inbox_attempts') <= " + failures + " THEN RAISE EXCEPTION USING ERRCODE='40001', MESSAGE='synthetic serialization'; END IF; RETURN NEW; END $$");
        await fixture.Database.DBClient.NonQueryAsync("CREATE CONSTRAINT TRIGGER reject_inbox_attempt AFTER UPDATE ON node DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status) EXECUTE FUNCTION reject_inbox_attempt()");
        Task<string> pending = stream.Frame();
        Task Patch() => fixture.Nodes.Patch(member, [new Pooshit.AspNetCore.Services.Patches.PatchOperation { Op = "replace", Path = "/status", Value = "changed" }], fixture.OwnerId, false, default);
        if (exhausted) {
            Assert.CatchAsync(Patch);
            Assert.That(fixture.PostgresScalar<long>("SELECT last_value FROM inbox_attempts"), Is.EqualTo(8));
            Assert.That(await Count(fixture), Is.Zero);
            Assert.That(pending.IsCompleted, Is.False);
            await fixture.Database.DBClient.NonQueryAsync("DROP TRIGGER reject_inbox_attempt ON node");
        }
        await Patch();
        Assert.That(await pending, Is.EqualTo("event:invalidate\ndata:{}"));
        Assert.That(await Count(fixture), Is.EqualTo(1));
        if (!exhausted)
            Assert.That(fixture.PostgresScalar<long>("SELECT last_value FROM inbox_attempts"), Is.EqualTo(3));
    }

    /// <summary>Checks lost HTTP confirmation does not suppress a database-committed inbox wakeup.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test, Parallelizable]
    public async Task InboxChanges_LostHttpConfirmation_CommittedRowStillWakes() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(postgres: true, configureServices: services => services.AddSingleton<IStartupFilter, InboxAbortResponseFilter>());
        await using NodeChangeFeedConnection stream = await Open(fixture);
        Assert.CatchAsync(() => fixture.HttpService().Post<MessageDetails, HttpResponseMessage>(Rest.Path(TestSetup.BaseUrl, "api", "messages"),
            new MessageDetails { RecipientId = fixture.OwnerId, Subject = "confirmation lost", Body = "private" }, fixture.Options()));
        Assert.That(await Count(fixture), Is.EqualTo(1));
        Assert.That(await stream.Frame(), Is.EqualTo("event:invalidate\ndata:{}"));
    }

    static Task<long> Insert(IEntityManager database, long recipient) => database.Insert<Message>()
        .Columns(row => row.AuthorId, row => row.RecipientId, row => row.Subject, row => row.Body, row => row.CreatedAt)
        .Values(0L, recipient, "private subject", "private body", DateTime.UtcNow).ReturnID().ExecuteAsync();

    /// <summary>Checks committed LISTEN precedes fresh HMAC reads and queued setup changes cannot be lost.</summary>
    /// <param name="pauseAt">The initial or ready admission authority read.</param>
    /// <param name="revoke">Whether the independently committed change rotates the key instead of inserting work.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase(2, false), TestCase(2, true), TestCase(3, true), Parallelizable]
    public async Task InboxChanges_ListenThenFreshHmac_SetupCommitCannotEscape(int pauseAt, bool revoke) {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(postgres: true);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        fixture.Probe.BeforeRead = sql => {
            if (sql.Contains("apikey", StringComparison.OrdinalIgnoreCase) && Interlocked.Increment(ref reads) == pauseAt) {
                entered.TrySetResult();
                return release.Task;
            }
            return Task.CompletedTask;
        };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using NodeChangeRequestHandler handler = new(fixture.Host.CreateHandler(), timeout.Token);
        IHttpService http = new HttpService(handler);
        HttpOptions options = fixture.Options();
        options.CompletionOption = HttpCompletionOption.ResponseHeadersRead;
        Task<HttpResponseMessage> opening = http.Get<HttpResponseMessage>(Path(), options);
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(Sessions(fixture), Is.EqualTo(1));
            if (revoke) {
                byte[] changed = new byte[32];
                await fixture.Database.Update<ApiKey>().Set(row => row.KeyHash == changed).Where(row => row.Id == fixture.Key.Id).ExecuteAsync();
            } else
                await Insert(fixture.Database, fixture.OwnerId);
        } finally {
            release.TrySetResult();
        }
        using HttpResponseMessage response = await opening;
        fixture.Probe.BeforeRead = null;
        if (revoke) {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(response.Content.Headers.ContentType!.MediaType, Is.Not.EqualTo("text/event-stream"));
        } else {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using StreamReader reader = new(await response.Content.ReadAsStreamAsync(timeout.Token));
            Assert.That(await reader.ReadLineAsync(timeout.Token), Is.EqualTo("event:ready"));
            Assert.That(await reader.ReadLineAsync(timeout.Token), Does.Contain("postgres-database"));
            Assert.That(await reader.ReadLineAsync(timeout.Token), Is.Empty);
            Assert.That(await reader.ReadLineAsync(timeout.Token), Is.EqualTo("event:invalidate"));
            Assert.That(await reader.ReadLineAsync(timeout.Token), Is.EqualTo("data:{}"));
        }
    }

    /// <summary>Checks missing producer installation fails closed without retaining a listener session.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test, Parallelizable]
    public async Task InboxChanges_MissingProducer_IsSafeUnavailable() {
        using NodeChangeFeedFixture fixture = await NodeChangeFeedFixture.CreateAsync(postgres: true);
        await fixture.Database.DBClient.NonQueryAsync("DROP TRIGGER divoid_inbox_v1 ON message");
        using HttpResponseMessage response = await fixture.HttpService().Get<HttpResponseMessage>(Path(), fixture.Options());
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("inbox_changes_unavailable"));
        Assert.That(Sessions(fixture), Is.Zero);
    }

    static long Sessions(NodeChangeFeedFixture fixture) => fixture.PostgresScalar<long>("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND application_name='divoid-inbox-events'");

    static async Task<NodeChangeFeedConnection> Open(NodeChangeFeedFixture fixture, ApiKeyDetails? key = null) {
        NodeChangeFeedConnection stream = await NodeChangeFeedConnection.OpenAsync(fixture.Host, Path(), (key ?? fixture.Key).PlaintextKey);
        Assert.That(await stream.Frame(), Is.EqualTo("event:ready\ndata:{\"version\":1,\"coverage\":\"postgres-database\",\"replay\":false,\"resyncRequired\":true}"));
        return stream;
    }

    static async Task Install(IEntityManager database) {
        using Transaction transaction = database.Transaction();
        await MessageChangeSchema.LockAsync(database, transaction);
        await MessageChangeSchema.InstallAsync(database, transaction);
        transaction.Commit();
    }

    static string Path() => Rest.Path(TestSetup.BaseUrl, "api", "v1", "messagechanges");

    static HttpOptions Options(string token) => new() { TokenProvider = new NodeChangeTokenProvider(token) };

    static Task<long> Count(NodeChangeFeedFixture fixture) => fixture.Database.Load<Message>(DB.Count()).ExecuteScalarAsync<long>();
}
