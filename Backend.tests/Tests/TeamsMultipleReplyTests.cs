using System.Net;
using Backend.Errors.Exceptions;
using Backend.Models.Messages;
using Backend.Models.Users;
using Backend.Services.Messages;
using Backend.Services.Teams;
using Backend.tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Pooshit.Http.Paths;
using Pooshit.Json;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Schemas;
using Pooshit.Ocelot.Tokens;

namespace Backend.tests.Tests;

/// <summary>Exercises independent logical claims through real Startup, HTTP and native Connector serialization.</summary>
[TestFixture, Parallelizable]
public class TeamsMultipleReplyTests {
    /// <summary>Checks multiple stages and identical bodies remain independently idempotent.</summary>
    /// <param name="postgres">Whether to use PostgreSQL.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task MultipleReplies_AcknowledgmentThenAnswer_UsesSameRetainedOrigin(bool postgres) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(postgres);
        using TeamsRoutingFixture fixture = new(database);
        long id = await Receive(fixture);
        Guid ack = Guid.NewGuid(), answer = Guid.NewGuid();
        using HttpResponseMessage missing = await fixture.ReplyStatus(id, ack);
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await Count<TeamsMessageReply>(database), Is.Zero);
        using HttpResponseMessage first = await fixture.CreateReply(id, ack, "same authorized text");
        using HttpResponseMessage repeat = await fixture.CreateReply(id, ack, "same authorized text");
        using HttpResponseMessage second = await fixture.CreateReply(id, answer, "same authorized text");
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(repeat.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(fixture.Host.Connector.Bodies, Has.Count.EqualTo(2));
        foreach (string payload in fixture.Host.Connector.Bodies) {
            Dictionary<string, object> sent = Json.Read<Dictionary<string, object>>(payload);
            Assert.That(sent["replyToId"], Is.EqualTo("activity-1"));
            Assert.That(((Dictionary<string, object>)sent["from"])["id"], Is.EqualTo("28:" + TeamsWebhookFixture.ClientId));
            Assert.That(((Dictionary<string, object>)sent["recipient"])["id"], Is.EqualTo("sender-1"));
        }
        Assert.That(fixture.Host.Connector.Destinations.All(uri => uri.AbsoluteUri.Contains("conversation-1")), Is.True);
        string publicJson = await second.Content.ReadAsStringAsync();
        Dictionary<string, object> receipt = Json.Read<Dictionary<string, object>>(publicJson);
        Assert.That(receipt.Keys, Is.EquivalentTo(new[] { "messageId", "replyId", "transport", "state", "outboundActivityId" }));
        Assert.That(Json.Read<MessageReplyDetails>(publicJson).ReplyId, Is.EqualTo(answer));
        Assert.That(await Count<TeamsMessageReply>(database), Is.EqualTo(2));
        Assert.That((await database.Entities.Load<TeamsMessageOrigin>().ExecuteEntityAsync()).ReplyState, Is.EqualTo(TeamsReplyState.Pending));
    }

    /// <summary>Checks consumed origins and current graph rebinding do not alter original ownership or expiry.</summary>
    /// <param name="postgres">Whether to use PostgreSQL.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task DelayedExpertReply_AfterConsumptionAndGraphRebind_UsesOriginalRecipient(bool postgres) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(postgres);
        using TeamsRoutingFixture fixture = new(database);
        long id = await Receive(fixture);
        using HttpResponseMessage ack = await fixture.CreateReply(id, Guid.NewGuid(), "acknowledgment");
        using HttpResponseMessage consumed = await fixture.Host.Send(HttpMethod.Delete, Rest.Path("api/messages", id), fixture.UserToken(database.RecipientId));
        Assert.That(consumed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        long? ago = DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds();
        long? expiry = ago + 30 * 24 * 60 * 60;
        await database.Entities.Update<TeamsMessageOrigin>().Set(origin => origin.ConsumedAt == ago, origin => origin.ExpiresAt == expiry).ExecuteAsync();
        await database.SetBindingAsync(database.BindingId, database.RecipientId, database.OtherId, "rebound");
        using TeamsRoutingFixture rebound = new(database);
        Guid key = Guid.NewGuid();
        using HttpResponseMessage denied = await rebound.CreateReply(id, key, userId: database.OtherId);
        using HttpResponseMessage deniedGet = await rebound.ReplyStatus(id, key, database.OtherId);
        using HttpResponseMessage answer = await rebound.CreateReply(id, key, "expert answer two days later");
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(deniedGet.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(answer.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        TeamsMessageOrigin retained = await database.Entities.Load<TeamsMessageOrigin>().ExecuteEntityAsync();
        Assert.That(retained.ConsumedAt, Is.EqualTo(ago));
        Assert.That(retained.ExpiresAt, Is.EqualTo(expiry));
        Assert.That(await Count<Message>(database), Is.Zero);
        Assert.That(rebound.Host.Connector.Bodies, Has.Count.EqualTo(1));
    }

    /// <summary>Checks competing requests use durable per-key ownership on independent connections.</summary>
    /// <param name="postgres">Whether to use PostgreSQL.</param>
    /// <param name="failedRetry">Whether callers compete for a previously rejected key.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(false, false), TestCase(true, false), TestCase(false, true), TestCase(true, true), Parallelizable]
    public async Task ReplyKey_ConcurrentSameBody_OneNetworkAttempt(bool postgres, bool failedRetry) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(postgres);
        using TeamsRoutingFixture fixture = new(database);
        using TeamsRoutingFixture independent = new(database);
        long id = await Receive(fixture);
        Guid key = Guid.NewGuid();
        if (failedRetry) {
            fixture.Host.Connector.Status = HttpStatusCode.BadRequest;
            using HttpResponseMessage rejected = await fixture.CreateReply(id, key);
            Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
            fixture.Host.Connector.Status = HttpStatusCode.OK;
        }
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Host.Connector.ResponseBarrier = release.Task;
        Task<HttpResponseMessage> first = fixture.CreateReply(id, key);
        // A previous rejected attempt already completed Started, so also wait for its new request.
        int expected = failedRetry ? 2 : 1;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        while (fixture.Host.Connector.Bodies.Count < expected)
            await Task.Delay(10, deadline.Token);
        using HttpResponseMessage concurrent = await independent.CreateReply(id, key);
        Assert.That(concurrent.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        release.SetResult();
        using HttpResponseMessage accepted = await first;
        using HttpResponseMessage same = await independent.CreateReply(id, key);
        Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(same.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(fixture.Host.Connector.Bodies, Has.Count.EqualTo(expected));
        Assert.That(independent.Host.Connector.Bodies, Is.Empty);
        Assert.That(await Count<TeamsMessageReply>(database), Is.EqualTo(1));
    }

    /// <summary>Checks body identity is immutable in all stored states and UUIDs are origin-scoped.</summary>
    /// <param name="state">The stored lifecycle state.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(TeamsReplyState.Pending), TestCase(TeamsReplyState.Failed), TestCase(TeamsReplyState.Sending), TestCase(TeamsReplyState.Unknown), TestCase(TeamsReplyState.Sent), Parallelizable]
    public async Task ReplyKey_DifferentBodyConflicts_InEveryPersistedState(TeamsReplyState state) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        using TeamsRoutingFixture fixture = new(database);
        long id = await Receive(fixture);
        Guid key = Guid.NewGuid();
        using HttpResponseMessage initial = await fixture.CreateReply(id, key);
        string saved = key.ToString("D");
        await database.Entities.Update<TeamsMessageReply>().Set(row => row.State == state).Where(row => row.ReplyId == saved).ExecuteAsync();
        using HttpResponseMessage changed = await fixture.CreateReply(id, key, "different body");
        Assert.That(changed.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(fixture.Host.Connector.Bodies, Has.Count.EqualTo(1));
        Dictionary<string, object?> activity = TeamsWebhookFixture.Activity();
        activity["id"] = "another-inbound";
        using HttpResponseMessage another = await fixture.Host.Post(fixture.Host.Token(), activity);
        Message second = await database.Entities.Load<Message>().Where(message => message.Id != id).ExecuteEntityAsync();
        using HttpResponseMessage scoped = await fixture.CreateReply(second.Id, key, "different body");
        Assert.That(scoped.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await Count<TeamsMessageReply>(database), Is.EqualTo(2));
    }

    /// <summary>Checks definitive rejection alone permits an explicit same-key/same-text retry.</summary>
    /// <param name="postgres">Whether to use PostgreSQL.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task ReplyKey_ExplicitFailedRetry_OnlySameKeyAndBody(bool postgres) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(postgres);
        using TeamsRoutingFixture fixture = new(database);
        long id = await Receive(fixture);
        Guid key = Guid.NewGuid();
        fixture.Host.Connector.Status = HttpStatusCode.BadRequest;
        using HttpResponseMessage failed = await fixture.CreateReply(id, key);
        Assert.That(failed.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
        Assert.That((await Stored(database, key)).State, Is.EqualTo(TeamsReplyState.Failed));
        fixture.Host.Connector.Status = HttpStatusCode.OK;
        using HttpResponseMessage altered = await fixture.CreateReply(id, key, "a changed retry");
        Assert.That(altered.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        using HttpResponseMessage accepted = await fixture.CreateReply(id, key);
        Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(fixture.Host.Connector.Bodies, Has.Count.EqualTo(2));
    }

    /// <summary>Checks uncertainty and stale claims cannot manufacture another attempt.</summary>
    /// <param name="scenario">The uncertain external or local failure.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase("server"), TestCase("cancel"), TestCase("incomplete"), TestCase("stale"), TestCase("receipt"), Parallelizable]
    public async Task ReplyKey_UnknownAndStaleSending_NeverReplay(string scenario) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        using TeamsRoutingFixture fixture = new(database);
        long id = await Receive(fixture);
        Guid key = Guid.NewGuid();
        if (scenario == "server") fixture.Host.Connector.Status = HttpStatusCode.ServiceUnavailable;
        if (scenario == "cancel") fixture.Host.Connector.CancelSend = true;
        if (scenario == "incomplete") fixture.Host.Connector.ResponseBody = "{}";
        if (scenario == "receipt")
            await database.Entities.DBClient.NonQueryAsync("CREATE TRIGGER fail_plural_receipt BEFORE UPDATE ON teamsmessagereply WHEN NEW.outboundactivityid IS NOT NULL BEGIN SELECT RAISE(ABORT, 'synthetic receipt persistence failure'); END");
        using HttpResponseMessage first = await fixture.CreateReply(id, key);
        Assert.That(first.StatusCode, Is.EqualTo(scenario == "stale" ? HttpStatusCode.OK : HttpStatusCode.BadGateway));
        if (scenario is "stale" or "receipt") {
            long? ago = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();
            string saved = key.ToString("D");
            await database.Entities.Update<TeamsMessageReply>().Set(row => row.State == TeamsReplyState.Sending, row => row.AttemptStartedAt == ago)
                .Where(row => row.ReplyId == saved).ExecuteAsync();
        }
        using HttpResponseMessage status = await fixture.ReplyStatus(id, key);
        Assert.That(Json.Read<MessageReplyDetails>(await status.Content.ReadAsStringAsync()).State, Is.EqualTo("unknown"));
        using HttpResponseMessage repeat = await fixture.CreateReply(id, key);
        Assert.That(repeat.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(fixture.Host.Connector.Bodies, Has.Count.EqualTo(1));
        if (scenario == "receipt")
            await database.Entities.DBClient.NonQueryAsync("DROP TRIGGER fail_plural_receipt");
        fixture.Host.Connector.Status = HttpStatusCode.OK;
        fixture.Host.Connector.CancelSend = false;
        fixture.Host.Connector.ResponseBody = "{\"id\":\"later-stage\"}";
        using HttpResponseMessage later = await fixture.CreateReply(id, Guid.NewGuid(), "a genuinely new stage");
        Assert.That(later.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    /// <summary>Checks a late confirmation cannot recreate children after atomic expiry cleanup.</summary>
    /// <param name="postgres">Whether to use PostgreSQL.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task ReplyExpiry_AtomicChildCleanupAndClaimRace_NoOrphans(bool postgres) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(postgres);
        using TeamsRoutingFixture fixture = new(database);
        long id = await Receive(fixture);
        Guid key = Guid.NewGuid();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Host.Connector.ResponseBarrier = release.Task;
        Task<HttpResponseMessage> send = fixture.CreateReply(id, key);
        await fixture.Host.Connector.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        long? expired = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await database.Entities.Update<TeamsMessageOrigin>().Set(origin => origin.ExpiresAt == expired).Where(origin => origin.MessageId == id).ExecuteAsync();
        using HttpResponseMessage gone = await fixture.ReplyStatus(id, key);
        Assert.That(gone.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await Count<TeamsMessageReply>(database), Is.Zero);
        Assert.That(await Count<TeamsMessageOrigin>(database), Is.Zero);
        release.SetResult();
        using HttpResponseMessage unknown = await send;
        Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
        using HttpResponseMessage newClaim = await fixture.CreateReply(id, Guid.NewGuid());
        Assert.That(newClaim.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await Count<TeamsMessageReply>(database), Is.Zero);
        Assert.That(fixture.Host.Connector.Bodies, Has.Count.EqualTo(1));
    }

    /// <summary>Checks legacy slots and historical ambiguity survive additive schema and plural sends.</summary>
    /// <param name="state">The legacy receipt state.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(TeamsReplyState.Pending), TestCase(TeamsReplyState.Sent), TestCase(TeamsReplyState.Unknown), TestCase(TeamsReplyState.Failed), Parallelizable]
    public async Task LegacyReply_AfterPluralSends_PreservesOriginalReceiptAndConflict(TeamsReplyState state) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        using TeamsRoutingFixture fixture = new(database);
        long id = await Receive(fixture);
        if (state is TeamsReplyState.Sent or TeamsReplyState.Failed) {
            fixture.Host.Connector.Status = state == TeamsReplyState.Failed ? HttpStatusCode.BadRequest : HttpStatusCode.OK;
            using HttpResponseMessage legacy = await fixture.Reply(id);
            fixture.Host.Connector.Status = HttpStatusCode.OK;
        } else if (state == TeamsReplyState.Unknown)
            await database.Entities.Update<TeamsMessageOrigin>().Set(origin => origin.ReplyState == state).ExecuteAsync();
        TeamsMessageOrigin before = await database.Entities.Load<TeamsMessageOrigin>().ExecuteEntityAsync();
        await new SchemaService(database.Entities.DBClient).CreateOrUpdateSchema<TeamsMessageReply>();
        using HttpResponseMessage plural = await fixture.CreateReply(id, Guid.NewGuid(), "new stage");
        Assert.That(plural.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(Json.WriteString(await database.Entities.Load<TeamsMessageOrigin>().ExecuteEntityAsync()), Is.EqualTo(Json.WriteString(before)));
        using HttpResponseMessage status = await fixture.Status(id);
        Assert.That(Json.Read<MessageReplyReceipt>(await status.Content.ReadAsStringAsync()).State, Is.EqualTo(state.ToString().ToLowerInvariant()));
        using HttpResponseMessage legacyAgain = await fixture.Reply(id);
        Assert.That(legacyAgain.StatusCode, Is.EqualTo(state == TeamsReplyState.Unknown ? HttpStatusCode.Conflict : HttpStatusCode.OK));
        if (state == TeamsReplyState.Sent) {
            using HttpResponseMessage altered = await fixture.Reply(id, "altered final");
            Assert.That(altered.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        }
    }

    /// <summary>Checks every authorized-origin guard applies to both additive HTTP methods.</summary>
    /// <param name="scenario">The disallowed saved reference or actor.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase("recipient"), TestCase("disabled"), TestCase("bot"), TestCase("tenant"), TestCase("url"), TestCase("expired"), TestCase("integration"), Parallelizable]
    public async Task MultipleReplyHttp_OriginalRecipientPoliciesAndNoOriginFields(string scenario) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        using TeamsRoutingFixture fixture = new(database);
        long id = await Receive(fixture);
        Guid key = Guid.NewGuid();
        if (scenario == "disabled")
            await database.Entities.Update<User>().Set(user => user.Enabled == false).Where(user => user.Id == database.RecipientId).ExecuteAsync();
        if (scenario == "bot")
            await database.Entities.Update<TeamsMessageOrigin>().Set(origin => origin.BotAppId == "another-bot").ExecuteAsync();
        if (scenario == "tenant")
            await database.Entities.Update<TeamsMessageOrigin>().Set(origin => origin.TenantId == "not-a-tenant").ExecuteAsync();
        if (scenario == "url")
            await database.Entities.Update<TeamsMessageOrigin>().Set(origin => origin.ServiceUrl == "http://unsafe.invalid").ExecuteAsync();
        if (scenario == "expired") {
            long? now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await database.Entities.Update<TeamsMessageOrigin>().Set(origin => origin.ExpiresAt == now).ExecuteAsync();
        }
        using TeamsRoutingFixture checkedHost = new(database, scenario == "integration" ? new() { ["Teams:Enabled"] = "false" } : null);
        long user = scenario == "recipient" ? database.OtherId : database.RecipientId;
        using HttpResponseMessage post = await checkedHost.CreateReply(id, key, userId: user);
        using HttpResponseMessage get = await checkedHost.ReplyStatus(id, key, user);
        Assert.That(post.StatusCode, Is.EqualTo(scenario == "disabled" ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound));
        Assert.That(get.StatusCode, Is.EqualTo(scenario == "disabled" ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound));
        Assert.That(checkedHost.Host.Connector.Bodies, Is.Empty);
        Assert.That(await Count<TeamsMessageReply>(database), Is.Zero);
    }

    /// <summary>Checks HTTP UUID and text validation never claims a send.</summary>
    /// <param name="scenario">The invalid field.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase("zero"), TestCase("malformed"), TestCase("blank"), TestCase("large"), Parallelizable]
    public async Task MultipleReplyHttp_InvalidKeyOrBody_NoNetwork(string scenario) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        using TeamsRoutingFixture fixture = new(database);
        long id = await Receive(fixture);
        string key = scenario == "zero" ? Guid.Empty.ToString("D") : scenario == "malformed" ? "broken" : Guid.NewGuid().ToString("D");
        string body = scenario == "blank" ? "  " : scenario == "large" ? new string('é', 8193) : "text";
        using HttpResponseMessage response = await fixture.Host.Send(HttpMethod.Post, Rest.Path("api/messages", id, "replies"), fixture.UserToken(database.RecipientId),
            new StringContent(Json.WriteString(new Dictionary<string, object> { ["replyId"] = key, ["body"] = body }), System.Text.Encoding.UTF8, "application/json"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(fixture.Host.Connector.Bodies, Is.Empty);
        Assert.That(await Count<TeamsMessageReply>(database), Is.Zero);
    }

    /// <summary>Checks the unique pair survives schema reapplication and failed PostgreSQL transactions are disposed before reads.</summary>
    /// <param name="postgres">Whether to use PostgreSQL.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task ReplyKey_UniquePair_RollbackPreservesReceipt(bool postgres) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(postgres);
        using TeamsRoutingFixture fixture = new(database);
        long id = await Receive(fixture);
        Guid key = Guid.NewGuid();
        using HttpResponseMessage first = await fixture.CreateReply(id, key);
        string saved = key.ToString("D");
        await new SchemaService(database.Entities.DBClient).CreateOrUpdateSchema<TeamsMessageReply>();
        Assert.CatchAsync(async () => await MessageService.Transact(database.Entities, async transaction => {
            await database.Entities.Insert<TeamsMessageReply>().Columns(row => row.MessageId, row => row.ReplyId)
                .Values(id, saved).ExecuteAsync(transaction);
            return true;
        }));
        using HttpResponseMessage receipt = await fixture.ReplyStatus(id, key);
        Assert.That(receipt.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(Json.Read<MessageReplyDetails>(await receipt.Content.ReadAsStringAsync()).State, Is.EqualTo("sent"));
        Assert.That(await Count<TeamsMessageReply>(database), Is.EqualTo(1));
    }

    /// <summary>Checks expiry winning a parent lock prevents a new child claim.</summary>
    /// <param name="postgres">Whether to use PostgreSQL.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task ReplyExpiry_ClaimWaitsForParentAndRechecksEligibility(bool postgres) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(postgres);
        using TeamsRoutingFixture fixture = new(database);
        long id = await Receive(fixture);
        Task<HttpResponseMessage> attempt;
        using (var transaction = database.Entities.Transaction()) {
            long? expired = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await database.Entities.Update<TeamsMessageOrigin>().Set(origin => origin.ExpiresAt == expired)
                .Where(origin => origin.MessageId == id).ExecuteAsync(transaction);
            attempt = Task.Run(() => fixture.CreateReply(id, Guid.NewGuid()));
            await Task.Delay(100);
            Assert.That(attempt.IsCompleted, Is.False);
            transaction.Commit();
        }
        using HttpResponseMessage response = await attempt.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await Count<TeamsMessageReply>(database), Is.Zero);
        Assert.That(fixture.Host.Connector.Bodies, Is.Empty);
    }

    /// <summary>Checks insert persistence failure cannot emit a request and the transaction releases its parent lock.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Test, Parallelizable]
    public async Task ReplyKey_ClaimPersistenceFailure_NoNetwork() {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        using TeamsRoutingFixture fixture = new(database);
        long id = await Receive(fixture);
        Guid key = Guid.NewGuid();
        await database.Entities.DBClient.NonQueryAsync("CREATE TRIGGER fail_plural_claim BEFORE INSERT ON teamsmessagereply BEGIN SELECT RAISE(ABORT, 'synthetic claim persistence failure'); END");
        using HttpResponseMessage failed = await fixture.CreateReply(id, key);
        Assert.That(failed.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That(fixture.Host.Connector.Bodies, Is.Empty);
        Assert.That(await Count<TeamsMessageReply>(database), Is.Zero);
        await database.Entities.DBClient.NonQueryAsync("DROP TRIGGER fail_plural_claim");
        using HttpResponseMessage accepted = await fixture.CreateReply(id, key);
        Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    /// <summary>Checks actual caller cancellation after native request capture preserves an uncertain claim.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Test, Parallelizable]
    public async Task ReplyKey_CallerCancelledAfterSend_NoReplay() {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        using TeamsRoutingFixture fixture = new(database);
        long id = await Receive(fixture);
        Guid key = Guid.NewGuid();
        fixture.Host.Connector.ResponseBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        using CancellationTokenSource cancellation = new();
        Task<MessageReplyDetails> attempt = fixture.Host.Services.GetRequiredService<TeamsMessageService>()
            .CreateReplyAsync(database.RecipientId, id, key, "reply text", cancellation.Token);
        await fixture.Host.Connector.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Assert.ThrowsAsync<MessageReplyException>(async () => await attempt);
        using HttpResponseMessage again = await fixture.CreateReply(id, key);
        Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await Stored(database, key)).State, Is.EqualTo(TeamsReplyState.Unknown));
        Assert.That(fixture.Host.Connector.Bodies, Has.Count.EqualTo(1));
    }

    /// <summary>Checks cleanup remains bounded while deleting children in the same transaction.</summary>
    /// <param name="postgres">Whether to use PostgreSQL.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task ReplyExpiry_BoundedBatch_RemovesOnlyExpiredChildren(bool postgres) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(postgres);
        using TeamsRoutingFixture fixture = new(database);
        long active = await Receive(fixture);
        long? expired = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (int index = 0; index < 70; ++index) {
            long id = 10000 + index;
            await database.Entities.Insert<TeamsMessageOrigin>()
                .Columns(origin => origin.MessageId, origin => origin.DedupKey, origin => origin.ExpiresAt)
                .Values(id, Guid.NewGuid().ToString("N"), expired).ExecuteAsync();
            await database.Entities.Insert<TeamsMessageReply>().Columns(row => row.MessageId, row => row.ReplyId)
                .Values(id, Guid.NewGuid().ToString("D")).ExecuteAsync();
        }
        using HttpResponseMessage status = await fixture.Status(active);
        Assert.That(status.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await Count<TeamsMessageOrigin>(database), Is.EqualTo(7));
        Assert.That(await Count<TeamsMessageReply>(database), Is.EqualTo(6));
        using HttpResponseMessage next = await fixture.Status(active);
        Assert.That(await Count<TeamsMessageOrigin>(database), Is.EqualTo(1));
        Assert.That(await Count<TeamsMessageReply>(database), Is.Zero);
    }

    /// <summary>Checks policy middleware preserves read/write permissions and malformed GET keys never mutate.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Test, Parallelizable]
    public async Task MultipleReplyHttp_ReadWritePoliciesAndMalformedGet() {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        using TeamsRoutingFixture fixture = new(database);
        long id = await Receive(fixture);
        Guid key = Guid.NewGuid();
        string read = Json.WriteString(new[] { "read" });
        await database.Entities.Update<User>().Set(user => user.Permissions == read).Where(user => user.Id == database.RecipientId).ExecuteAsync();
        using HttpResponseMessage deniedWrite = await fixture.CreateReply(id, key);
        using HttpResponseMessage missingRead = await fixture.ReplyStatus(id, key);
        Assert.That(deniedWrite.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(missingRead.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        using HttpResponseMessage malformed = await fixture.Host.Send(HttpMethod.Get, Rest.Path("api/messages", id, "replies", "not-a-uuid"), fixture.UserToken(database.RecipientId));
        Assert.That(malformed.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        string write = Json.WriteString(new[] { "write" });
        await database.Entities.Update<User>().Set(user => user.Permissions == write).Where(user => user.Id == database.RecipientId).ExecuteAsync();
        using HttpResponseMessage deniedRead = await fixture.ReplyStatus(id, key);
        using HttpResponseMessage accepted = await fixture.CreateReply(id, key);
        // Existing permission hierarchy is admin -> write -> read.
        Assert.That(deniedRead.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(fixture.Host.Connector.Bodies, Has.Count.EqualTo(1));
    }

    /// <summary>Checks the exact byte limit and invalid UTF-8 before a logical claim.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Test, Parallelizable]
    public async Task MultipleReplyHttp_Utf8ByteBoundaryAndInvalidText() {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        using TeamsRoutingFixture fixture = new(database);
        long id = await Receive(fixture);
        TeamsMessageService service = fixture.Host.Services.GetRequiredService<TeamsMessageService>();
        Assert.ThrowsAsync<System.Text.EncoderFallbackException>(async () =>
            await service.CreateReplyAsync(database.RecipientId, id, Guid.NewGuid(), "\ud800", CancellationToken.None));
        Assert.That(await Count<TeamsMessageReply>(database), Is.Zero);
        Assert.That(fixture.Host.Connector.Bodies, Is.Empty);
        using HttpResponseMessage exact = await fixture.CreateReply(id, Guid.NewGuid(), new string('é', 8192));
        Assert.That(exact.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(fixture.Host.Connector.Bodies, Has.Count.EqualTo(1));
    }

    static async Task<long> Receive(TeamsRoutingFixture fixture) {
        using HttpResponseMessage response = await fixture.Host.Post(fixture.Host.Token());
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await fixture.Database.Entities.Load<Message>().ExecuteEntityAsync()).Id;
    }

    static Task<long> Count<T>(TeamsRoutingDatabase database) => database.Entities.Load<T>(DB.Count()).ExecuteScalarAsync<long>();

    static Task<TeamsMessageReply> Stored(TeamsRoutingDatabase database, Guid replyId) {
        string key = replyId.ToString("D");
        return database.Entities.Load<TeamsMessageReply>().Where(row => row.ReplyId == key).ExecuteEntityAsync();
    }
}
