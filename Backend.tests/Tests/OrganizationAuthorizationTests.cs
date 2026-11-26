using Backend.Models.Nodes;
using Backend.Models.Organizations;
using Backend.Models.Users;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Expressions;
using Pooshit.Ocelot.Tokens;

namespace Backend.tests.Tests;

/// <summary>Checks the access contract on actual SQLite and PostgreSQL predicates.</summary>
[TestFixture, Parallelizable]
public class OrganizationAuthorizationTests {
    /// <summary>Checks all known flags against the independent access table.</summary>
    /// <param name="postgres">Whether PostgreSQL executes the predicate.</param>
    /// <returns>The asynchronous operation.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task Visibility_AllFlagsAndMemberships_MatchesContractAndSnapshot(bool postgres) {
        using DatabaseFixture? sqlite = postgres ? null : new();
        if (postgres && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")))
            Assert.Ignore("Private POSTGRES_CONNECTION is required.");
        using EmbeddingPostgresDatabase? pg = postgres ? new() : null;
        if (pg != null)
            await pg.Start();
        IEntityManager database = pg?.Entities ?? sqlite!.EntityManager;
        long caller = await database.Insert<User>().Columns(u => u.Name, u => u.Enabled, u => u.CreatedAt).Values("caller", true, DateTime.UtcNow).ReturnID().ExecuteAsync();
        long node = await database.Insert<Node>().Columns(n => n.TypeId, n => n.OwnerId, n => n.Access).Values(0L, 999L, NodeAccess.None).ReturnID().ExecuteAsync();
        bool[] globalRead = [false,true,false,true,false,true,false,true,false,true,false,true,false,true,false,true];
        bool[] globalWrite = [false,false,true,true,false,false,true,true,false,false,true,true,false,false,true,true];
        bool[] sameRead = [false,true,false,true,true,true,true,true,false,true,false,true,true,true,true,true];
        bool[] sameWrite = [false,false,true,true,false,false,true,true,true,true,true,true,true,true,true,true];
        (long? User, long? Node, bool Same)[] memberships = [(1,1,true),(1,2,false),(1,null,false),(null,1,false),(null,null,false)];
        foreach ((long? userOrg, long? nodeOrg, bool same) in memberships) {
            await database.Update<User>().Set(u => u.OrganizationId == userOrg).Where(u => u.Id == caller).ExecuteAsync();
            for (int flags = 0; flags < 16; ++flags) {
                NodeAccess access = (NodeAccess)flags;
                await database.Update<Node>().Set(n => n.Access == access, n => n.OrganizationId == nodeOrg).Where(n => n.Id == node).ExecuteAsync();
                foreach (bool write in new[] { false, true }) {
                    bool expected = write ? (same ? sameWrite[flags] : globalWrite[flags]) : (same ? sameRead[flags] : globalRead[flags]);
                    PredicateExpression<Node> visibility = NodeAuthorization.BuildVisibilityPredicate(database, caller, false, write);
                    long count = await database.Load<Node>(DB.Count()).Where(visibility.Content).ExecuteScalarAsync<long>();
                    Assert.That(count, Is.EqualTo(expected ? 1 : 0), $"flags={flags},userOrg={userOrg},nodeOrg={nodeOrg},write={write}");
                    Assert.That(NodeAuthorization.IsAllowed(999, access, nodeOrg, caller, false, userOrg, write), Is.EqualTo(expected));
                    Assert.That(NodeAuthorization.IsAllowed(caller, access, nodeOrg, caller, false, userOrg, write), Is.True);
                    Assert.That(NodeAuthorization.IsAllowed(999, access, nodeOrg, caller, true, userOrg, write), Is.True);
                    Assert.That(NodeAuthorization.BuildVisibilityPredicate(database, caller, true, write), Is.Null);
                    PredicateExpression<Node> owner = NodeAuthorization.BuildVisibilityPredicate(database, 999, false, write);
                    Assert.That(await database.Load<Node>(DB.Count()).Where(owner.Content).ExecuteScalarAsync<long>(), Is.EqualTo(1));
                }
            }
        }
    }
}
