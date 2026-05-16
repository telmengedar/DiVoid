using Backend.Init;
using Backend.Models.Messages;
using Backend.Models.Users;
using Backend.tests.Fixtures;
using Pooshit.Ocelot.Schemas;

namespace Backend.tests.Tests;

[TestFixture]
public class DatabaseModelServiceTests
{
    /// <summary>
    /// Verify the schema bootstrap creates all expected tables in a fresh database.
    /// </summary>
    [Test]
    public async Task StartAsync_FreshDatabase_CreatesAllTables()
    {
        using DatabaseFixture fixture = new();
        ISchemaService schema = new SchemaService(fixture.EntityManager.DBClient);

        // If the tables didn't exist we'd get exceptions when loading.  Instead we
        // verify by running a scalar count query against each table.
        long nodeCount = await fixture.EntityManager.Load<Backend.Models.Nodes.Node>(Pooshit.Ocelot.Tokens.DB.Count())
                                      .ExecuteScalarAsync<long>();
        long nodeTypeCount = await fixture.EntityManager.Load<Backend.Models.Nodes.NodeType>(Pooshit.Ocelot.Tokens.DB.Count())
                                          .ExecuteScalarAsync<long>();
        long nodeLinkCount = await fixture.EntityManager.Load<Backend.Models.Nodes.NodeLink>(Pooshit.Ocelot.Tokens.DB.Count())
                                          .ExecuteScalarAsync<long>();
        long apiKeyCount = await fixture.EntityManager.Load<Backend.Models.Auth.ApiKey>(Pooshit.Ocelot.Tokens.DB.Count())
                                        .ExecuteScalarAsync<long>();
        long userCount = await fixture.EntityManager.Load<User>(Pooshit.Ocelot.Tokens.DB.Count())
                                      .ExecuteScalarAsync<long>();
        long messageCount = await fixture.EntityManager.Load<Message>(Pooshit.Ocelot.Tokens.DB.Count())
                                         .ExecuteScalarAsync<long>();

        Assert.Multiple(() => {
            Assert.That(nodeCount, Is.EqualTo(0));
            Assert.That(nodeTypeCount, Is.EqualTo(0));
            Assert.That(nodeLinkCount, Is.EqualTo(0));
            Assert.That(apiKeyCount, Is.EqualTo(0));
            Assert.That(userCount, Is.EqualTo(0));
            Assert.That(messageCount, Is.EqualTo(0),
                "Message table must exist after schema bootstrap — a zero count confirms the table was created");
        });
    }

    /// <summary>
    /// A second StartAsync call on the same database must not throw (idempotent).
    /// </summary>
    [Test]
    public async Task StartAsync_CalledTwice_IsIdempotent()
    {
        using DatabaseFixture fixture = new();
        DatabaseModelService svc = new(fixture.EntityManager);

        // First call is already done by the fixture; call a second time.
        Assert.DoesNotThrowAsync(() => svc.StartAsync(CancellationToken.None));
        await Task.CompletedTask; // keep async signature satisfied
    }
}
