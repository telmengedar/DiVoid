using Backend.Models.Nodes;
using Backend.Models.Organizations;
using Backend.Models.Users;
using Backend.Services.Nodes;
using Backend.Services.Organizations;
using Backend.Services.Users;
using Backend.Services.Embeddings;
using Backend.tests.Fixtures;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Tokens;
using static Backend.tests.Fixtures.OrganizationTestEnvironment;

namespace Backend.tests.Tests;

/// <summary>Checks concurrent PostgreSQL organization assignment and deletion.</summary>
[TestFixture, Parallelizable]
public class OrganizationReferenceConcurrencyTests {
    /// <summary>Checks both lock orders cannot commit a dangling organization reference.</summary>
    /// <param name="deleteFirst">Whether deletion acquires the parent lock first.</param>
    /// <param name="nodeAssignment">Whether node assignment competes instead of user assignment.</param>
    /// <returns>The asynchronous operation.</returns>
    [TestCase(false, false), TestCase(true, false), TestCase(false, true), TestCase(true, true), Parallelizable]
    public async Task Delete_ConcurrentAssignment_OneSideRejectsWithoutDanglingReference(bool deleteFirst, bool nodeAssignment) {
        using OrganizationTestEnvironment environment = await OrganizationTestEnvironment.Create(true);
        long organization = (await environment.Organizations.Create(new Organization { Name = "competing" })).Id;
        long user = await environment.User(null);
        NodeDetails node = await environment.Graph.Nodes.CreateNode(new NodeDetails { Name = "unassigned" }, user);
        IEntityManager heldDatabase = new EntityManager(OrganizationReferenceBarrier.Wrap(environment.Database.DBClient, out OrganizationReferenceBarrier barrier));
        OrganizationService heldOrganizations = new(heldDatabase);
        UserService heldUsers = new(heldDatabase, environment.Graph.Feed);
        NodeService heldNodes = new(heldDatabase, new EmbeddingCapability(false), EmbeddingProducers.Function, environment.Graph.Feed);
        Func<Task> heldAssign = nodeAssignment
            ? () => heldNodes.Patch(node.Id, [Patch("/organizationId", organization)], user, true, default)
            : () => heldUsers.UpdateUser(user, Patch("/organizationId", organization));
        Func<Task> ordinaryAssign = nodeAssignment
            ? () => environment.Graph.Nodes.Patch(node.Id, [Patch("/organizationId", organization)], user, true, default)
            : () => environment.Assign(user, organization);
        Task<Exception?> first = Capture(deleteFirst ? () => heldOrganizations.Delete(organization) : heldAssign);
        await barrier.Locked.WaitAsync(TimeSpan.FromSeconds(5));
        Task<Exception?> second = Capture(deleteFirst ? ordinaryAssign : () => environment.Organizations.Delete(organization));
        barrier.Release();
        Exception?[] outcomes = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(outcomes[0], Is.Null);
        Assert.That(outcomes[1], Is.Not.Null);
        long exists = await environment.Database.Load<Organization>(DB.Count()).Where(o => o.Id == organization).ExecuteScalarAsync<long>();
        long users = await environment.Database.Load<User>(DB.Count()).Where(u => u.OrganizationId == organization).ExecuteScalarAsync<long>();
        long nodes = await environment.Database.Load<Node>(DB.Count()).Where(n => n.OrganizationId == organization).ExecuteScalarAsync<long>();
        Assert.That(exists, Is.EqualTo(deleteFirst ? 0 : 1));
        Assert.That(users + nodes, Is.EqualTo(deleteFirst ? 0 : 1));
    }

    static async Task<Exception?> Capture(Func<Task> action) {
        try {
            await action();
            return null;
        } catch (Exception exception) {
            return exception;
        }
    }
}
