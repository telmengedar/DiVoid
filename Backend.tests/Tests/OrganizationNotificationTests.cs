using Backend.Models.Auth;
using Backend.Models.Messages;
using Backend.Models.Nodes;
using Backend.Services.NodeChanges;
using Backend.tests.Fixtures;
using Pooshit.AspNetCore.Services.Errors.Exceptions;
using Pooshit.Ocelot.Tokens;
using static Backend.tests.Fixtures.OrganizationTestEnvironment;

namespace Backend.tests.Tests;

/// <summary>Checks organization-aware hooks and committed stream revocation.</summary>
[TestFixture, Parallelizable]
public class OrganizationNotificationTests {
    /// <summary>Checks hook delivery uses the recipient's current persisted membership.</summary>
    /// <param name="postgres">Whether PostgreSQL is selected.</param>
    /// <returns>The asynchronous operation.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task Hooks_UserAndNodeOrganizationMoves_StopProtectedDelivery(bool postgres) {
        using OrganizationTestEnvironment environment = await OrganizationTestEnvironment.Create(postgres);
        long observer = await environment.User(environment.OrganizationId);
        NodeDetails watched = await environment.Graph.Nodes.CreateNode(new NodeDetails { Name = "protected", Status = "open", Access = NodeAccess.OrgRead }, environment.Graph.OwnerId);
        await environment.Graph.Nodes.RegisterHook(new NodeHook { NodeId = watched.Id, UserId = observer, Tag = "org", Events = NodeHookEvents.Status }, observer, false);
        await environment.Graph.Nodes.Patch(watched.Id, [Patch("/status", "first-private")], environment.Graph.OwnerId, false, default);
        Message initial = await environment.Database.Load<Message>().Where(m => m.RecipientId == observer).ExecuteEntityAsync();
        Assert.That(initial.Body, Does.Contain("first-private"));
        await environment.Database.Delete<Message>().ExecuteAsync();
        await environment.Assign(observer, environment.OtherOrganizationId);
        await environment.Graph.Nodes.Patch(watched.Id, [Patch("/status", "second-private")], environment.Graph.OwnerId, false, default);
        Assert.That(await environment.Database.Load<Message>(DB.Count()).Where(m => m.RecipientId == observer).ExecuteScalarAsync<long>(), Is.Zero);
        Assert.ThrowsAsync<NotFoundException<Node>>(() => environment.Graph.Nodes.RegisterHook(new NodeHook { NodeId = watched.Id, UserId = observer, Tag = "denied" }, observer, false));
        await environment.Assign(observer, environment.OrganizationId);
        await environment.Graph.Nodes.Patch(watched.Id, [Patch("/organizationId", environment.OtherOrganizationId), Patch("/status", "third-private")], environment.Graph.OwnerId, true, default);
        Message revoked = await environment.Database.Load<Message>().Where(m => m.RecipientId == observer).ExecuteEntityAsync();
        Assert.That(revoked.Body, Is.EqualTo($"Access changed for node #{watched.Id}."));
        Assert.That(revoked.Body, Does.Not.Contain("third-private"));
        await environment.Database.Delete<Message>().ExecuteAsync();
        await environment.Graph.Nodes.Patch(watched.Id, [Patch("/access", "Read")], environment.Graph.OwnerId, false, default);
        await environment.Database.Delete<Message>().ExecuteAsync();
        await environment.Graph.Nodes.Patch(watched.Id, [Patch("/status", "global-visible")], environment.Graph.OwnerId, false, default);
        Message global = await environment.Database.Load<Message>().Where(m => m.RecipientId == observer).ExecuteEntityAsync();
        Assert.That(global.Body, Does.Contain("global-visible"));
    }

    /// <summary>Checks queued stream facts cannot be admitted after membership revocation.</summary>
    /// <param name="postgres">Whether PostgreSQL is selected.</param>
    /// <returns>The asynchronous operation.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task Feed_UserMoveAndNodeMove_RevokePendingAuthority(bool postgres) {
        using OrganizationTestEnvironment environment = await OrganizationTestEnvironment.Create(postgres);
        long observer = await environment.User(environment.OrganizationId, "[\"admin\"]");
        ApiKeyDetails read = await environment.Graph.AddKeyAsync(observer, ["read"]);
        await environment.Graph.Nodes.Patch(environment.Graph.RootId, [Patch("/organizationId", environment.OrganizationId), Patch("/access", "OrgRead")], environment.Graph.OwnerId, true, default);
        NodeDetails member = await environment.Graph.Nodes.CreateNode(new NodeDetails { Name = "member", RootNodeId = environment.Graph.RootId, Access = NodeAccess.OrgRead }, environment.Graph.OwnerId);
        NodeChangeFilter filter = new() { RootNodeId = environment.Graph.RootId };
        await using NodeChangeSubscription subscription = await environment.Graph.Feed.SubscribeAsync(environment.Database, read.Id, observer, filter, default);
        Assert.That(subscription.Admin, Is.False);
        await environment.Graph.Nodes.Patch(member.Id, [Patch("/name", "queued-private")], environment.Graph.OwnerId, false, default);
        Assert.That(subscription.Reader.TryRead(out bool dirty) && dirty, Is.True);
        await environment.Assign(observer, environment.OtherOrganizationId);
        Assert.That(subscription.Closed, Is.True);
        Assert.That(await environment.Graph.Feed.AdmitAsync(subscription, default), Is.False);
        Assert.ThrowsAsync<NotFoundException<Node>>(() => environment.Graph.Feed.SubscribeAsync(environment.Database, read.Id, observer, filter, default));
        await environment.Assign(observer, environment.OrganizationId);
        await using NodeChangeSubscription restored = await environment.Graph.Feed.SubscribeAsync(environment.Database, read.Id, observer, filter, default);
        await environment.Graph.Nodes.Patch(member.Id, [Patch("/organizationId", environment.OtherOrganizationId)], environment.Graph.OwnerId, true, default);
        Assert.That(restored.Reader.TryRead(out bool removed) && removed, Is.True);
        await environment.Graph.Nodes.Patch(member.Id, [Patch("/name", "hidden-after-move")], environment.Graph.OwnerId, false, default);
        Assert.That(restored.Reader.TryRead(out _), Is.False);
        await environment.Graph.Nodes.Patch(environment.Graph.RootId, [Patch("/organizationId", environment.OtherOrganizationId)], environment.Graph.OwnerId, true, default);
        Assert.That(restored.Closed, Is.True);
        Assert.That(await environment.Graph.Feed.AdmitAsync(restored, default), Is.False);
        await environment.Graph.Nodes.Patch(environment.Graph.RootId, [Patch("/access", "Read")], environment.Graph.OwnerId, false, default);
        await using NodeChangeSubscription global = await environment.Graph.Feed.SubscribeAsync(environment.Database, read.Id, observer, filter, default);
        await environment.Graph.Nodes.Patch(member.Id, [Patch("/access", "Read"), Patch("/name", "global-member")], environment.Graph.OwnerId, false, default);
        Assert.That(global.Reader.TryRead(out bool visible) && visible, Is.True);
        Assert.That(global.Closed, Is.False);
    }
}
