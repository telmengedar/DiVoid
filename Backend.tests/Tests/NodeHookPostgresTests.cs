using Backend.Models.Messages;
using Backend.Models.Nodes;
using Backend.Models.Users;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using Npgsql;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Tokens;

namespace Backend.tests.Tests;

/// <summary>PostgreSQL hook schema, concurrency and transaction guards</summary>
[TestFixture]
public class NodeHookPostgresTests {
    EmbeddingPostgresDatabase database = null!;
    IEntityManager db = null!;
    NodeService service = null!;
    long userId;
    long nodeId;

    /// <summary>setup</summary>
    [SetUp]
    public async Task Setup() {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("POSTGRES_CONNECTION"))) Assert.Ignore("POSTGRES_CONNECTION is required");
        database = new();
        await database.Start();
        db = database.Entities;
        service = new(db, new EmbeddingCapability(false), EmbeddingProducers.Function);
        userId = await db.Insert<User>().Columns(u => u.Name, u => u.Enabled, u => u.CreatedAt).Values("subscriber", true, DateTime.UtcNow).ReturnID().ExecuteAsync();
        nodeId = (await service.CreateNode(new NodeDetails { Name = "watched", Status = "open" }, userId)).Id;
    }

    /// <summary>teardown</summary>
    [TearDown]
    public void Teardown() => database?.Dispose();

    Task<NodeHook> Register(string tag = "route") => service.RegisterHook(new NodeHook { NodeId = nodeId, UserId = userId, Tag = tag }, userId, false);

    async Task<Message[]> Messages() {
        List<Message> messages = [];
        await foreach (Message message in db.Load<Message>().OrderBy(m => m.Id).ExecuteEntitiesAsync()) messages.Add(message);
        return messages.ToArray();
    }

    static async Task<string?> Attempt(Func<Task> action) {
        try {
            await action();
            return null;
        }
        catch (Exception exception) {
            Exception? current = exception;
            while (current != null) {
                if (current is PostgresException postgres && postgres.SqlState == "40001") return postgres.SqlState;
                current = current.InnerException;
            }
            throw;
        }
    }

    /// <summary>composite tuple is unique and case sensitive</summary>
    [Test]
    public async Task CompositeTuple_IsUniqueAndCaseSensitive() {
        await Register();
        await Register();
        await Register("Route");
        Assert.That(await db.Load<NodeHook>(DB.Count()).ExecuteScalarAsync<long>(), Is.EqualTo(2));
        Assert.CatchAsync(async () => await db.Insert<NodeHook>().Columns(h => h.NodeId, h => h.UserId, h => h.Tag).Values(nodeId, userId, "route").ExecuteAsync());
        await service.Patch(nodeId, [new PatchOperation { Op = "replace", Path = "/status", Value = "closed" }], userId, false, default);
        Assert.That(await Messages(), Has.Length.EqualTo(2));
    }

    /// <summary>concurrent status updates report actual serialized old and new</summary>
    [Test]
    public async Task ConcurrentStatusUpdates_ReportActualSerializedOldAndNew() {
        await Register();
        Task<string?>[] attempts = Enumerable.Range(0, 8).Select(index => Task.Run(() => Attempt(async () => {
            await service.Patch(nodeId, [new PatchOperation { Op = "replace", Path = "/status", Value = $"state-{index}" }], userId, false, default);
        }))).ToArray();
        string?[] outcomes = await Task.WhenAll(attempts);
        Message[] messages = await Messages();
        Assert.That(messages.Length, Is.EqualTo(outcomes.Count(result => result == null)));
        Assert.That(messages, Is.Not.Empty);
        string previous = "open";
        foreach (Message message in messages) {
            string line = message.Body.Split('\n').Single(part => part.StartsWith("Status changed:"));
            Assert.That(line, Does.StartWith($"Status changed: {previous} → "));
            previous = line.Split(" → ")[1];
        }
        Assert.That((await service.GetNodeById(nodeId, userId, false)).Status, Is.EqualTo(previous));
    }

    /// <summary>concurrent registration does not duplicate tuple</summary>
    [Test]
    public async Task ConcurrentRegistration_DoesNotDuplicateTuple() {
        Task<string?>[] attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(() => Attempt(async () => { await Register(); }))).ToArray();
        string?[] outcomes = await Task.WhenAll(attempts);
        Assert.That(outcomes.Any(result => result == null), Is.True);
        Assert.That(await db.Load<NodeHook>(DB.Count()).ExecuteScalarAsync<long>(), Is.EqualTo(1));
    }

    /// <summary>message failure rolls back status and prior messages</summary>
    [Test]
    public async Task MessageFailure_RollsBackStatusAndPriorMessages() {
        await Register();
        await Register("second");
        await db.DBClient.NonQueryAsync("CREATE FUNCTION reject_hook() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.subject LIKE '[second]%' THEN RAISE EXCEPTION 'test failure'; END IF; RETURN NEW; END $$");
        await db.DBClient.NonQueryAsync("CREATE TRIGGER reject_message BEFORE INSERT ON message FOR EACH ROW EXECUTE FUNCTION reject_hook()");
        Assert.CatchAsync(async () => await service.Patch(nodeId, [new PatchOperation { Op = "replace", Path = "/status", Value = "closed" }], userId, false, default));
        Assert.That((await service.GetNodeById(nodeId, userId, false)).Status, Is.EqualTo("open"));
        Assert.That(await Messages(), Is.Empty);
    }

    /// <summary>delete racing link has no dangling edge or lost committed notice</summary>
    [Test]
    public async Task DeleteRacingLink_HasNoDanglingEdgeOrLostCommittedNotice() {
        long neighborId = (await service.CreateNode(new NodeDetails { Name = "neighbor" }, userId)).Id;
        await service.RegisterHook(new NodeHook { NodeId = neighborId, UserId = userId, Tag = "neighbor" }, userId, false);
        Task<string?> link = Task.Run(() => Attempt(async () => {
            try {
                await service.LinkNodes(neighborId, nodeId, userId, false);
            }
            catch (Pooshit.AspNetCore.Services.Errors.Exceptions.NotFoundException<Node>) {
            }
        }));
        string? deleteOutcome = await Attempt(() => service.Delete(nodeId, userId, false));
        await link;
        if (deleteOutcome != null) await service.Delete(nodeId, userId, false);
        Assert.That(await db.Load<NodeLink>(DB.Count()).ExecuteScalarAsync<long>(), Is.Zero);
        Message[] messages = await Messages();
        Assert.That(messages.Count(m => m.Body.Contains("Link added")), Is.EqualTo(messages.Count(m => m.Body.Contains("Link removed"))));
        Assert.That(await db.Load<Node>(DB.Count()).Where(n => n.Id == nodeId).ExecuteScalarAsync<long>(), Is.Zero);
    }

    /// <summary>register remove races with mutation has commit ordered delivery</summary>
    [Test]
    public async Task RegisterRemove_RacesWithMutation_HasCommitOrderedDelivery() {
        await db.DBClient.NonQueryAsync("CREATE TABLE expected_hook_delivery (expected bigint NOT NULL)");
        await db.DBClient.NonQueryAsync($"CREATE FUNCTION observe_hook_delivery() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN INSERT INTO expected_hook_delivery SELECT count(*) FROM nodehook WHERE nodeid=NEW.id AND userid={userId}; RETURN NEW; END $$");
        await db.DBClient.NonQueryAsync("CREATE TRIGGER observe_status AFTER UPDATE ON node FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status) EXECUTE FUNCTION observe_hook_delivery()");
        NodeHook hook = new() { NodeId = nodeId, UserId = userId, Tag = "route" };
        for (int index = 0; index < 8; index++) {
            bool remove = index % 2 != 0;
            if (remove) await Register();
            else await service.DeleteHook(hook, userId, false);
            Task<string?> subscription = Task.Run(() => Attempt(async () => {
                if (remove) await service.DeleteHook(hook, userId, false);
                else await Register();
            }));
            string state = $"state-{index}";
            string? mutation = await Attempt(async () => {
                await service.Patch(nodeId, [new PatchOperation { Op = "replace", Path = "/status", Value = state }], userId, false, default);
            });
            await subscription;
            if (mutation != null)
                await service.Patch(nodeId, [new PatchOperation { Op = "replace", Path = "/status", Value = state }], userId, false, default);
        }
        long expected = database.Scalar<long>("SELECT COALESCE(sum(expected), 0)::bigint FROM expected_hook_delivery");
        Assert.That((await Messages()).LongLength, Is.EqualTo(expected));
        Assert.That(database.Scalar<long>("SELECT count(*) FROM expected_hook_delivery"), Is.EqualTo(8));
    }
}
