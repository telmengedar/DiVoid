#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.Init;
using Backend.Models.Organizations;
using Backend.Models.Users;
using Backend.tests.Fixtures;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Info;
using Pooshit.Ocelot.Schemas;
using Pooshit.Ocelot.Tokens;

namespace Backend.tests.Tests;

/// <summary>
/// verifies the first-boot bootstrap block in <see cref="DatabaseModelService"/>
/// seeds the "DiVoid" org at id 1 and backfills membership for every existing user.
/// </summary>
[TestFixture]
public class OrganizationBootstrapTests
{

    [Test]
    public async Task BootstrapSeedsDivoidOrganizationAtIdOne()
    {
        using DatabaseFixture fixture = new();
        IEntityManager db = fixture.EntityManager;

        long orgCount = await db.Load<Organization>(DB.Count()).ExecuteScalarAsync<long>();
        Assert.That(orgCount, Is.GreaterThanOrEqualTo(1L),
            "bootstrap must seed at least one Organization row on first boot");

        Organization bootstrap = await db.Load<Organization>()
                                          .Where(o => o.Id == Organization.BootstrapOrgIdConst)
                                          .ExecuteEntityAsync();
        Assert.That(bootstrap, Is.Not.Null,
            "the bootstrap Organization row at the const id must exist");
        Assert.That(bootstrap.Name, Is.EqualTo("DiVoid"),
            "the bootstrap Organization must be named 'DiVoid'");
    }

    [Test]
    public async Task BootstrapBackfillsMembershipForUsersPresentAtFirstBoot()
    {
        using SqliteConnection conn = new("Data Source=:memory:");
        conn.Open();
        IEntityManager seededDb = new EntityManager(ClientFactory.Create(conn, new SQLiteInfo()));

        SchemaService initialSchema = new(seededDb.DBClient);
        await initialSchema.CreateOrUpdateSchema<User>();

        long seededUserId = await seededDb.Insert<User>()
                                          .Columns(u => u.Name, u => u.Email, u => u.Enabled, u => u.CreatedAt)
                                          .Values("preboot-user", "preboot@test.com", true, DateTime.UtcNow)
                                          .ReturnID()
                                          .ExecuteAsync();

        DatabaseModelService bootstrap = new(seededDb);
        await bootstrap.StartAsync(CancellationToken.None);

        UserOrganization[] memberships = (await seededDb.Load<UserOrganization>()
                                                         .Where(m => m.UserId == seededUserId)
                                                         .ExecuteEntitiesAsync()
                                                         .ToArrayAsync()).ToArray();

        Assert.That(memberships, Is.Not.Empty,
            "users present in the DB before first boot must be backfilled into the bootstrap org");
        Assert.That(memberships[0].OrganizationId, Is.EqualTo(Organization.BootstrapOrgIdConst),
            "backfill membership must target the bootstrap org");
    }

    [Test]
    public async Task NewNodeWithoutOrganizationIdGetsBootstrapDefault()
    {
        using DatabaseFixture fixture = new();
        IEntityManager db = fixture.EntityManager;

        long typeId = await db.Insert<Backend.Models.Nodes.NodeType>()
                              .Columns(t => t.Type)
                              .Values("smoke-test-type")
                              .ReturnID()
                              .ExecuteAsync();

        long nodeId = await db.Insert<Backend.Models.Nodes.Node>()
                              .Columns(n => n.TypeId, n => n.Name)
                              .Values(typeId, "smoke-test-node")
                              .ReturnID()
                              .ExecuteAsync();

        Backend.Models.Nodes.Node fetched = await db.Load<Backend.Models.Nodes.Node>()
                                                     .Where(n => n.Id == nodeId)
                                                     .ExecuteEntityAsync();

        Assert.That(fetched.OrganizationId, Is.EqualTo(Organization.BootstrapOrgIdConst),
            "a new Node row that omits OrganizationId must default to the bootstrap org");
    }
}
