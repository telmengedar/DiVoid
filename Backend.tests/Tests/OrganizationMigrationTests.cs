using Backend.Init;
using Backend.Models.Nodes;
using Backend.Models.Organizations;
using Backend.Models.Users;
using Backend.tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Info;
using Pooshit.Ocelot.Tokens;

namespace Backend.tests.Tests;

/// <summary>Checks empty and populated pre-organization database upgrades.</summary>
[TestFixture, Parallelizable]
public class OrganizationMigrationTests {
    /// <summary>Checks nullable additions preserve historical data and remain idempotent.</summary>
    /// <param name="postgres">Whether PostgreSQL is selected.</param>
    /// <param name="populated">Whether the pre-feature database contains rows.</param>
    /// <returns>The asynchronous operation.</returns>
    [TestCase(false, false), TestCase(false, true), TestCase(true, false), TestCase(true, true), Parallelizable]
    public async Task Schema_PreFeatureDatabase_NullableNoBackfillPreservesRows(bool postgres, bool populated) {
        if (postgres && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")))
            Assert.Ignore("Private POSTGRES_CONNECTION is required.");
        using EmbeddingPostgresDatabase? pg = postgres ? new() : null;
        using SqliteConnection? sqlite = postgres ? null : new("Data Source=:memory:");
        sqlite?.Open();
        IEntityManager database = pg?.Entities ?? new EntityManager(ClientFactory.Create(sqlite!, new SQLiteInfo()));
        string identity = postgres ? "BIGSERIAL PRIMARY KEY" : "INTEGER PRIMARY KEY AUTOINCREMENT";
        string text = postgres ? "VARCHAR" : "TEXT";
        string blob = postgres ? "BYTEA" : "BLOB";
        string timestamp = postgres ? "TIMESTAMP" : "DATETIME";
        await database.DBClient.NonQueryAsync($"CREATE TABLE divoid_user (id {identity}, name {text}, email {text}, enabled BOOLEAN NOT NULL, createdat {timestamp} NOT NULL, permissions {text}, homenodeid BIGINT)");
        await database.DBClient.NonQueryAsync($"CREATE TABLE node (id {identity}, typeid BIGINT NOT NULL, name {text}, content {blob}, contenttype {text}, ownerid BIGINT NOT NULL DEFAULT 0, access INTEGER NOT NULL DEFAULT 3, created {timestamp} NOT NULL DEFAULT '0001-01-01 00:00:00', lastupdate {timestamp} NOT NULL DEFAULT '0001-01-01 00:00:00')");
        if (populated) {
            await database.DBClient.NonQueryAsync("INSERT INTO divoid_user (id,name,enabled,createdat,permissions,homenodeid) VALUES (5,'historical',true,'2020-01-01','[\"read\"]',71)");
            await database.DBClient.NonQueryAsync("INSERT INTO node (id,typeid,name,ownerid,access,created,lastupdate) VALUES (71,1,'private',5,0,'2020-01-01','2021-01-01'), (72,1,'global',9,3,'2020-01-01','2021-01-01')");
        }
        if (populated)
            await database.DBClient.NonQueryAsync("UPDATE node SET content=@1, contenttype='text/plain' WHERE id=71", new object[] { "historical-content"u8.ToArray() });
        DatabaseModelService schema = new(database, NullLogger<DatabaseModelService>.Instance);
        await schema.StartAsync(default);
        Assert.That(await database.Load<Organization>(DB.Count()).ExecuteScalarAsync<long>(), Is.Zero);
        Assert.That(await database.Load<User>(DB.Count()).ExecuteScalarAsync<long>(), Is.EqualTo(populated ? 1 : 0));
        Assert.That(await database.Load<Node>(DB.Count()).ExecuteScalarAsync<long>(), Is.EqualTo(populated ? 2 : 0));
        if (populated) {
            User user = await database.Load<User>().Where(u => u.Id == 5).ExecuteEntityAsync();
            Assert.That(user.OrganizationId, Is.Null);
            Assert.That(user.Name, Is.EqualTo("historical"));
            Assert.That(user.HomeNodeId, Is.EqualTo(71));
            foreach (long id in new[] { 71L, 72L }) {
                Node node = await database.Load<Node>(n => n.Id, n => n.Name, n => n.OwnerId, n => n.Access, n => n.OrganizationId, n => n.Content, n => n.ContentType, n => n.Created, n => n.LastUpdate).Where(n => n.Id == id).ExecuteEntityAsync();
                Assert.That(node.OrganizationId, Is.Null);
                Assert.That(node.Name, Is.EqualTo(id == 71 ? "private" : "global"));
                Assert.That(node.OwnerId, Is.EqualTo(id == 71 ? 5 : 9));
                Assert.That(node.Access, Is.EqualTo(id == 71 ? NodeAccess.None : NodeAccess.Read | NodeAccess.Write));
                Assert.That(node.Created, Is.EqualTo(new DateTime(2020, 1, 1)));
                Assert.That(node.LastUpdate, Is.EqualTo(new DateTime(2021, 1, 1)));
                if (id == 71) {
                    Assert.That(node.Content, Is.EqualTo("historical-content"u8.ToArray()));
                    Assert.That(node.ContentType, Is.EqualTo("text/plain"));
                }
            }
        }
        await schema.StartAsync(default);
        Assert.That(await database.Load<Organization>(DB.Count()).ExecuteScalarAsync<long>(), Is.Zero);
        Assert.That(await database.Load<Node>(DB.Count()).Where(n => n.OrganizationId != null).ExecuteScalarAsync<long>(), Is.Zero);
        Assert.That(await database.Load<User>(DB.Count()).Where(u => u.OrganizationId != null).ExecuteScalarAsync<long>(), Is.Zero);
    }
}
