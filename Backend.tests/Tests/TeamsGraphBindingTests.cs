using System.Net;
using System.Text;
using Backend.Models.Messages;
using Backend.Models.Nodes;
using Backend.Models.Users;
using Backend.Services.Nodes;
using Backend.Services.Teams;
using Backend.tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Pooshit.Json;
using Pooshit.Ocelot.Tokens;

namespace Backend.tests.Tests;

/// <summary>Exercises protected graph authority with real provider-backed persistence and HTTP ingress.</summary>
[TestFixture, Parallelizable]
public class TeamsGraphBindingTests {
    /// <summary>Checks bounded SQL projection and exact graph proof on both supported providers.</summary>
    /// <param name="postgres">Whether to use the disposable PostgreSQL fixture.</param>
    /// <param name="access">The protected record access mode.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(false, NodeAccess.Read), TestCase(true, NodeAccess.Read)]
    [TestCase(false, NodeAccess.None), TestCase(true, NodeAccess.None), Parallelizable]
    public async Task ResolveAsync_ProtectedBinding_ReturnsExactRecipientAndTag(bool postgres, NodeAccess access) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(postgres);
        using TeamsRoutingFixture fixture = new(database);
        await database.Entities.Update<Node>().Set(node => node.Access == access).Where(node => node.Id == database.BindingId).ExecuteAsync();
        (long recipient, string tag) = await fixture.Host.Services.GetRequiredService<ITeamsGraphBindingService>()
            .ResolveAsync(null, CancellationToken.None);
        Assert.That(recipient, Is.EqualTo(database.RecipientId));
        Assert.That(tag, Is.EqualTo("divoid"));
    }

    /// <summary>Checks the inclusive receiving-tag limit on both providers.</summary>
    /// <param name="postgres">Whether to exercise PostgreSQL.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task ResolveAsync_TagAtInclusiveLimit_ReturnsExactToken(bool postgres) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(postgres);
        string expectedTag = new('t', 128);
        await database.SetBindingAsync(database.BindingId, database.RecipientId, database.RecipientId, expectedTag);
        Assert.That(expectedTag, Has.Length.EqualTo(128));
        using TeamsRoutingFixture fixture = new(database);
        (long recipient, string tag) = await fixture.Host.Services.GetRequiredService<ITeamsGraphBindingService>()
            .ResolveAsync(null, CancellationToken.None);
        Assert.That(recipient, Is.EqualTo(database.RecipientId));
        Assert.That(tag, Is.EqualTo(expectedTag));
    }

    /// <summary>Checks authority rejects modified persisted identity, malformed envelopes and unsafe actor state.</summary>
    /// <param name="failure">The independently applied invalid binding condition.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase("missing"), TestCase("owner-transfer"), TestCase("public-write"), TestCase("owner-disabled"), TestCase("owner-missing")]
    [TestCase("target-disabled"), TestCase("target-missing"), TestCase("target-fraction"), TestCase("target-string")]
    [TestCase("proof-missing"), TestCase("proof-version"), TestCase("proof-length"), TestCase("proof-uppercase")]
    [TestCase("secret"), TestCase("bot"), TestCase("tag-empty"), TestCase("tag-space"), TestCase("tag-bracket")]
    [TestCase("tag-comma"), TestCase("tag-newline"), TestCase("tag-length"), TestCase("json"), TestCase("utf8")]
    [TestCase("oversize"), TestCase("type"), TestCase("name")]
    [TestCase("array"), TestCase("scalar"), TestCase("number"), TestCase("null"), Parallelizable]
    public async Task ReceiveAsync_InvalidAuthority_FailsClosedBeforePersistence(string failure) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        using TeamsRoutingFixture fixture = new(database);
        Node original = await database.Entities.Load<Node>(node => node.Content).Where(node => node.Id == database.BindingId).ExecuteEntityAsync();
        Dictionary<string, object> fields = Json.Read<Dictionary<string, object>>(Encoding.UTF8.GetString(original.Content));
        Assert.That(fields.ContainsKey("proof"), Is.True);
        switch (failure) {
            case "missing": await database.Entities.Delete<Node>().Where(node => node.Id == database.BindingId).ExecuteAsync(); break;
            case "owner-transfer": await database.Entities.Update<Node>().Set(node => node.OwnerId == database.OtherId).Where(node => node.Id == database.BindingId).ExecuteAsync(); break;
            case "public-write": await database.Entities.Update<Node>().Set(node => node.Access == NodeAccess.Write).Where(node => node.Id == database.BindingId).ExecuteAsync(); break;
            case "owner-disabled":
            case "owner-missing":
                long owner = await database.AddUserAsync(true, ["read", "write"]);
                await database.Entities.Update<Node>().Set(node => node.OwnerId == owner).Where(node => node.Id == database.BindingId).ExecuteAsync();
                await database.SetBindingAsync(database.BindingId, owner, database.RecipientId, "divoid");
                if (failure == "owner-missing")
                    await database.Entities.Delete<User>().Where(user => user.Id == owner).ExecuteAsync();
                else
                    await database.Entities.Update<User>().Set(user => user.Enabled == false).Where(user => user.Id == owner).ExecuteAsync();
                break;
            case "target-disabled": fields["userId"] = database.BridgeId; break;
            case "target-missing": fields["userId"] = long.MaxValue; break;
            case "target-fraction": fields["userId"] = 1.5; break;
            case "target-string": fields["userId"] = database.RecipientId.ToString(); break;
            case "proof-missing": fields.Remove("proof"); break;
            case "proof-version": fields["proof"] = ((string)fields["proof"]).Replace("v1:", "v2:"); break;
            case "proof-length": fields["proof"] = "hmac-sha256-v1:00"; break;
            case "proof-uppercase": fields["proof"] = ((string)fields["proof"]).ToUpperInvariant(); break;
            case "secret": await database.SetBindingAsync(database.BindingId, database.RecipientId, database.RecipientId, "divoid", "other-secret"); break;
            case "bot": fields["botId"] = Guid.NewGuid().ToString("D"); break;
            case "tag-empty": fields["tag"] = ""; break;
            case "tag-space": fields["tag"] = "other tag"; break;
            case "tag-bracket": fields["tag"] = "[other]"; break;
            case "tag-comma": fields["tag"] = "other,tag"; break;
            case "tag-newline": fields["tag"] = "other\ntag"; break;
            case "tag-length": fields["tag"] = new string('x', 129); break;
            case "type": await database.Entities.Update<Node>().Set(node => node.TypeId == 0L).Where(node => node.Id == database.BindingId).ExecuteAsync(); break;
            case "name": await database.Entities.Update<Node>().Set(node => node.Name == "other-bot").Where(node => node.Id == database.BindingId).ExecuteAsync(); break;
        }
        if (failure is not ("missing" or "owner-transfer" or "public-write" or "owner-disabled" or "owner-missing" or "secret" or "type" or "name")) {
            byte[] content = failure switch {
                "json" => Encoding.UTF8.GetBytes("{broken"),
                "array" => Encoding.UTF8.GetBytes("[]"),
                "scalar" => Encoding.UTF8.GetBytes("\"text\""),
                "number" => Encoding.UTF8.GetBytes("17"),
                "null" => Encoding.UTF8.GetBytes("null"),
                "utf8" => [0xff],
                "oversize" => Encoding.UTF8.GetBytes(new string('x', 8193)),
                _ => Encoding.UTF8.GetBytes(Json.WriteString(fields))
            };
            await WriteContent(database, database.BindingId, content);
        }
        using HttpResponseMessage response = await fixture.Host.Post(fixture.Host.Token());
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable), failure);
        Assert.That(await Count<Message>(database), Is.Zero);
        Assert.That(await Count<TeamsMessageOrigin>(database), Is.Zero);
        Assert.That(fixture.Host.Connector.Bodies, Is.Empty);
    }

    /// <summary>Checks copied proof cannot redirect and cryptographically authorized duplicates never select a winner.</summary>
    /// <param name="postgres">Whether to exercise PostgreSQL.</param>
    /// <param name="duplicate">Whether to authorize a second node, including an invalid-target duplicate.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(false, "clone"), TestCase(true, "clone")]
    [TestCase(false, "clone-node"), TestCase(true, "clone-node")]
    [TestCase(false, "valid"), TestCase(true, "valid")]
    [TestCase(false, "invalid-target"), TestCase(true, "invalid-target"), Parallelizable]
    public async Task ReceiveAsync_ClonesAndAuthorizedDuplicates_UseProofCardinality(bool postgres, string duplicate) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(postgres);
        using TeamsRoutingFixture fixture = new(database);
        long second = await database.AddBindingAsync(duplicate == "clone-node" ? database.RecipientId : database.OtherId, database.OtherId, "other");
        Assert.That(second, Is.Not.EqualTo(database.BindingId));
        if (duplicate.StartsWith("clone", StringComparison.Ordinal)) {
            Node original = await database.Entities.Load<Node>(node => node.Content).Where(node => node.Id == database.BindingId).ExecuteEntityAsync();
            await WriteContent(database, second, original.Content);
        } else if (duplicate == "invalid-target")
            await database.SetBindingAsync(second, database.OtherId, -1, "invalid tag");
        using HttpResponseMessage response = await fixture.Host.Post(fixture.Host.Token());
        Assert.That(response.StatusCode, Is.EqualTo(duplicate.StartsWith("clone", StringComparison.Ordinal) ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable));
        Assert.That(await Count<Message>(database), Is.EqualTo(duplicate.StartsWith("clone", StringComparison.Ordinal) ? 1 : 0));
        Assert.That(await Count<TeamsMessageOrigin>(database), Is.EqualTo(duplicate.StartsWith("clone", StringComparison.Ordinal) ? 1 : 0));
        if (duplicate.StartsWith("clone", StringComparison.Ordinal)) {
            Message message = await database.Entities.Load<Message>().ExecuteEntityAsync();
            Assert.That(message.RecipientId, Is.EqualTo(database.RecipientId));
            Assert.That(message.AuthorId, Is.Zero);
        }
        Assert.That(fixture.Host.Connector.Bodies, Is.Empty);
    }

    /// <summary>Checks that bot identity in the proof comes from configured authority rather than body fields.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Test, Parallelizable]
    public async Task ReceiveAsync_CrossBotCopy_CannotReuseOriginalProof() {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        string otherBot = Guid.NewGuid().ToString("D");
        Node original = await database.Entities.Load<Node>(node => node.Content).Where(node => node.Id == database.BindingId).ExecuteEntityAsync();
        Dictionary<string, object> fields = Json.Read<Dictionary<string, object>>(Encoding.UTF8.GetString(original.Content));
        fields["botId"] = otherBot;
        await WriteContent(database, database.BindingId, Encoding.UTF8.GetBytes(Json.WriteString(fields)));
        await database.Entities.Update<Node>().Set(node => node.Name == otherBot).Where(node => node.Id == database.BindingId).ExecuteAsync();
        using TeamsRoutingFixture fixture = new(database, new() { ["AzureAd:ClientId"] = otherBot });
        using HttpResponseMessage response = await fixture.Host.Post(fixture.Host.Token(audience: otherBot));
        Assert.That(fixture.Host.Metadata.KeyRequests, Is.GreaterThan(0));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(await Count<Message>(database), Is.Zero);
        Assert.That(fixture.Host.Connector.Bodies, Is.Empty);
    }

    /// <summary>Checks exact secret bytes and the inclusive UTF-8 content limit on each provider.</summary>
    /// <param name="postgres">Whether to exercise PostgreSQL.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task ResolveAsync_ContentBoundaryAndUntrimmedSecret_AcceptsOnlyBoundedEnvelope(bool postgres) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(postgres);
        const string Secret = " synthetic-fixture-secret ";
        await database.SetBindingAsync(database.BindingId, database.RecipientId, database.RecipientId, "divoid", Secret);
        Node node = await database.Entities.Load<Node>(value => value.Content).Where(value => value.Id == database.BindingId).ExecuteEntityAsync();
        string json = Encoding.UTF8.GetString(node.Content);
        byte[] boundary = Encoding.UTF8.GetBytes(json + new string(' ', 8192 - node.Content.Length));
        Assert.That(boundary, Has.Length.EqualTo(8192));
        await WriteContent(database, database.BindingId, boundary);
        using TeamsRoutingFixture fixture = new(database, new() { ["AzureAd:ClientCredentials:0:ClientSecret"] = Secret });
        ITeamsGraphBindingService resolver = fixture.Host.Services.GetRequiredService<ITeamsGraphBindingService>();
        (long recipient, _) = await resolver.ResolveAsync(null, CancellationToken.None);
        Assert.That(recipient, Is.EqualTo(database.RecipientId));
        await WriteContent(database, database.BindingId, boundary.Concat(new byte[] { 32 }).ToArray());
        Assert.ThrowsAsync<Backend.Errors.Exceptions.MessageReplyException>(async () => await resolver.ResolveAsync(null, CancellationToken.None));
    }

    /// <summary>Checks owner edits through the production node ACL apply without host restart or proof refresh.</summary>
    /// <param name="postgres">Whether to exercise PostgreSQL.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task ReceiveAsync_LiveOwnerEdit_ChangesNextRouteAndPreservesOriginalReply(bool postgres) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(postgres);
        using TeamsRoutingFixture fixture = new(database);
        using HttpResponseMessage first = await fixture.Host.Post(fixture.Host.Token());
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Message original = await database.Entities.Load<Message>().ExecuteEntityAsync();
        Node record = await database.Entities.Load<Node>(node => node.Content).Where(node => node.Id == database.BindingId).ExecuteEntityAsync();
        Dictionary<string, object> fields = Json.Read<Dictionary<string, object>>(Encoding.UTF8.GetString(record.Content));
        string proof = (string)fields["proof"];
        long nextRecipient = await database.AddUserAsync(true, ["read", "write"]);
        fields["userId"] = nextRecipient;
        fields["tag"] = "next-session";
        byte[] updated = Encoding.UTF8.GetBytes(Json.WriteString(fields));
        INodeService nodes = fixture.Host.Services.GetRequiredService<INodeService>();
        await using (MemoryStream deniedBody = new(updated)) {
            Assert.ThrowsAsync<Pooshit.AspNetCore.Services.Errors.Exceptions.NotFoundException<Node>>(async () =>
                await nodes.UploadContent(database.BindingId, "application/json", deniedBody, nextRecipient, false));
        }
        Node unchanged = await database.Entities.Load<Node>(node => node.Content).Where(node => node.Id == database.BindingId).ExecuteEntityAsync();
        Assert.That(unchanged.Content, Is.EqualTo(record.Content));
        await using (MemoryStream body = new(updated))
            await nodes.UploadContent(database.BindingId, "application/json", body, database.RecipientId, false);
        Node changed = await database.Entities.Load<Node>(node => node.Content).Where(node => node.Id == database.BindingId).ExecuteEntityAsync();
        Assert.That(Json.Read<Dictionary<string, object>>(Encoding.UTF8.GetString(changed.Content))["proof"], Is.EqualTo(proof));
        Dictionary<string, object?> activity = TeamsWebhookFixture.Activity();
        activity["id"] = "next-activity";
        using HttpResponseMessage second = await fixture.Host.Post(fixture.Host.Token(), activity);
        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Message next = await database.Entities.Load<Message>().Where(message => message.Id != original.Id).ExecuteEntityAsync();
        Assert.That(next.RecipientId, Is.EqualTo(nextRecipient));
        Assert.That(next.Subject, Is.EqualTo("[next-session] Teams message"));
        using HttpResponseMessage deniedReply = await fixture.Reply(original.Id, userId: nextRecipient);
        using HttpResponseMessage acceptedReply = await fixture.Reply(original.Id);
        Assert.That(deniedReply.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(acceptedReply.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(fixture.Host.Connector.Destinations.Single().AbsoluteUri, Does.Contain("conversation-1"));
    }

    /// <summary>Checks later binding invalidity does not revoke an original recipient's private return path.</summary>
    /// <param name="change">The later graph-authority change.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase("delete"), TestCase("owner"), TestCase("ambiguity"), TestCase("secret"), Parallelizable]
    public async Task ReplyAsync_LaterGraphAuthorityChange_UsesOriginalRecipientSnapshot(string change) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        using TeamsRoutingFixture fixture = new(database);
        using HttpResponseMessage first = await fixture.Host.Post(fixture.Host.Token());
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Message original = await database.Entities.Load<Message>().ExecuteEntityAsync();
        if (change == "delete")
            await database.Entities.Delete<Node>().Where(node => node.Id == database.BindingId).ExecuteAsync();
        else if (change == "owner")
            await database.Entities.Update<Node>().Set(node => node.OwnerId == database.OtherId).Where(node => node.Id == database.BindingId).ExecuteAsync();
        else if (change == "ambiguity")
            await database.AddBindingAsync(database.OtherId, database.OtherId, "other");
        else
            await database.SetBindingAsync(database.BindingId, database.RecipientId, database.RecipientId, "divoid", "rotated-secret");
        Dictionary<string, object?> activity = TeamsWebhookFixture.Activity();
        activity["id"] = "new-activity";
        using HttpResponseMessage rejected = await fixture.Host.Post(fixture.Host.Token(), activity);
        Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        using HttpResponseMessage denied = await fixture.Reply(original.Id, userId: database.OtherId);
        using HttpResponseMessage reply = await fixture.Reply(original.Id);
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(reply.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(fixture.Host.Connector.Bodies, Has.Count.EqualTo(1));
    }

    /// <summary>Checks configured-secret rotation rejects old proof and accepts only the refreshed node-bound proof.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Test, Parallelizable]
    public async Task ReceiveAsync_SecretRotation_RequiresFreshProofButPreservesOriginalReply() {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        using TeamsRoutingFixture originalHost = new(database);
        using HttpResponseMessage first = await originalHost.Host.Post(originalHost.Host.Token());
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Message original = await database.Entities.Load<Message>().ExecuteEntityAsync();
        using TeamsRoutingFixture rotated = new(database, new() { ["AzureAd:ClientCredentials:0:ClientSecret"] = "rotated-secret" });
        Dictionary<string, object?> activity = TeamsWebhookFixture.Activity();
        activity["id"] = "rotated-activity";
        using HttpResponseMessage rejected = await rotated.Host.Post(rotated.Host.Token(), activity);
        Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        using HttpResponseMessage originalReply = await rotated.Reply(original.Id);
        Assert.That(originalReply.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await database.SetBindingAsync(database.BindingId, database.RecipientId, database.RecipientId, "divoid", "rotated-secret");
        using HttpResponseMessage accepted = await rotated.Host.Post(rotated.Host.Token(), activity);
        Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await Count<Message>(database), Is.EqualTo(2));
    }

    /// <summary>Checks the inclusive candidate limit and rejects populations that exceed it.</summary>
    /// <param name="postgres">Whether to exercise PostgreSQL.</param>
    /// <param name="population">The actual number of matching graph records.</param>
    /// <param name="accepted">Whether ingress must accept the bounded population.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(false, 128, true), TestCase(true, 128, true)]
    [TestCase(false, 129, false), TestCase(true, 129, false), Parallelizable]
    public async Task ReceiveAsync_CandidateBound_AcceptsOnlySafePopulation(bool postgres, int population, bool accepted) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(postgres);
        using TeamsRoutingFixture fixture = new(database);
        for (int index = 1; index < population; index++)
            await database.AddBindingAsync(database.OtherId, database.OtherId, "forged", "wrong-secret");
        Assert.That(await Count<Node>(database), Is.EqualTo(population));
        using HttpResponseMessage response = await fixture.Host.Post(fixture.Host.Token());
        Assert.That(response.StatusCode, Is.EqualTo(accepted ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable));
        Assert.That(await Count<Message>(database), Is.EqualTo(accepted ? 1 : 0));
        Assert.That(await Count<TeamsMessageOrigin>(database), Is.EqualTo(accepted ? 1 : 0));
        if (accepted) {
            Message message = await database.Entities.Load<Message>().ExecuteEntityAsync();
            Assert.That(message.RecipientId, Is.EqualTo(database.RecipientId));
            Assert.That(message.Subject, Is.EqualTo("[divoid] Teams message"));
        }
        Assert.That(fixture.Host.Connector.Bodies, Is.Empty);
    }

    static Task WriteContent(TeamsRoutingDatabase database, long id, byte[] content) => database.Entities.Update<Node>()
        .Set(node => node.Content == content).Where(node => node.Id == id).ExecuteAsync();

    static Task<long> Count<T>(TeamsRoutingDatabase database) => database.Entities.Load<T>(DB.Count()).ExecuteScalarAsync<long>();
}
