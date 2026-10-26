using Backend.Models.Nodes;
using Backend.tests.Fixtures;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Schemas;
using Pooshit.Ocelot.Tokens;

namespace Backend.tests.Tests;

/// <summary>node hook schema upgrade guards</summary>
[TestFixture]
public class NodeHookSchemaUpgradeTests {
    /// <summary>existing hook rows gain the all events default</summary>
    [Test]
    public async Task CreateOrUpdateSchema_ExistingHookRows_GainEventsAll() {
        using DatabaseFixture fixture = new();
        IEntityManager db = fixture.EntityManager;
        await db.DBClient.NonQueryAsync("DROP TABLE nodehook");
        await db.DBClient.NonQueryAsync("CREATE TABLE nodehook (nodeid INTEGER NOT NULL, userid INTEGER NOT NULL, tag TEXT NOT NULL)");
        await db.DBClient.NonQueryAsync("INSERT INTO nodehook (nodeid, userid, tag) VALUES (7, 3, 'route')");
        Assert.That(await db.Load<NodeHook>(DB.Count()).ExecuteScalarAsync<long>(), Is.EqualTo(1));

        using Transaction transaction = db.Transaction();
        await new SchemaService(db.DBClient).CreateOrUpdateSchema<NodeHook>(transaction);
        transaction.Commit();

        NodeHook hook = await db.Load<NodeHook>().ExecuteEntityAsync();
        Assert.That(hook.Events, Is.EqualTo(NodeHookEvents.All));
    }
}
