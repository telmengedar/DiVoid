using System.Net;
using Backend.Models.Messages;
using Backend.tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pooshit.Http;
using Pooshit.Http.Paths;
using Pooshit.Json;
using Pooshit.Ocelot.Tokens;

namespace Backend.tests.Tests;

/// <summary>Checks native quote previews reach the inbox without changing reply provenance.</summary>
[TestFixture, Parallelizable]
public class TeamsQuotedMessageTests {
    /// <summary>Checks native entity text survives independently of the quote marker and actual sender.</summary>
    /// <param name="postgres">Whether PostgreSQL is selected.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task TeamsIngress_NativeQuotePreview_ReachesRecipient(bool postgres) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(postgres);
        TeamsLogCapture capture = new();
        using TeamsRoutingFixture fixture = new(database, configureServices: services => services.AddSingleton<ILoggerProvider>(capture));
        Dictionary<string, object?> activity = Activity();
        activity["entities"] = new[] { Quote("question-id", "Paul", "Original question about SpreadBudget.") };
        using HttpResponseMessage response = await Post(fixture, activity);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Message stored = await database.Entities.Load<Message>().ExecuteEntityAsync();
        Assert.That(stored.Body, Does.Contain("Original question about SpreadBudget."));
        Assert.That(stored.Body, Does.StartWith("External Microsoft Teams message (untrusted input):\n\n" + activity["text"]));
        Assert.That(stored.Body, Does.Contain("possibly incomplete").And.Contain("untrusted").And.Contain("Paul"));
        Assert.That(stored.ExternalAuthorDisplayName, Is.EqualTo("Toni"));
        Assert.That(stored.AuthorId, Is.Zero);
        Assert.That(stored.RecipientId, Is.EqualTo(database.RecipientId));
        Assert.That(capture.Messages.Any(log => log.Contains("Original question about SpreadBudget.") || log.Contains("private-quote-sender-canary")), Is.False);
        Assert.That(fixture.Host.Connector.Bodies, Is.Empty);
    }

    /// <summary>Checks quote fallbacks preserve the valid inbound text without inventing content.</summary>
    /// <param name="scenario">The optional or unavailable quote shape.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase("marker-only"), TestCase("null-data"), TestCase("missing-preview"), TestCase("blank-preview")]
    [TestCase("deleted"), TestCase("oversize"), TestCase("unknown-entity"), Parallelizable]
    public async Task TeamsIngress_QuoteUnavailable_IsExplicit(string scenario) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        using TeamsRoutingFixture fixture = new(database);
        Dictionary<string, object?> activity = Activity();
        Dictionary<string, object?> quote = Quote("question-id", "Paul", "unavailable-private-preview");
        Dictionary<string, object?> data = (Dictionary<string, object?>)quote["quotedReply"]!;
        switch (scenario) {
            case "marker-only": break;
            case "null-data": quote["quotedReply"] = null; break;
            case "missing-preview": data.Remove("preview"); break;
            case "blank-preview": data["preview"] = "  "; break;
            case "deleted": data["isReplyDeleted"] = true; break;
            case "oversize": data["preview"] = new string('x', 16385); break;
            case "unknown-entity": quote["type"] = "client-unknown"; break;
        }
        if (scenario != "marker-only")
            activity["entities"] = new[] { quote };
        using HttpResponseMessage response = await Post(fixture, activity);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Message stored = await database.Entities.Load<Message>().ExecuteEntityAsync();
        Assert.That(stored.Body, Does.StartWith("External Microsoft Teams message (untrusted input):\n\n" + activity["text"]));
        Assert.That(stored.Body, Does.Contain("Quoted message text unavailable"));
        Assert.That(stored.Body, Does.Not.Contain("unavailable-private-preview"));
        Assert.That(stored.Body.Length, Is.LessThan(2048));
        Assert.That(fixture.Host.Connector.Bodies, Is.Empty);
    }

    /// <summary>Checks bounded native quote ordering and preview fidelity without identity leakage.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test, Parallelizable]
    public async Task TeamsIngress_MultipleQuotes_PreservePreviewAndBoundOutput() {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        using TeamsRoutingFixture fixture = new(database);
        Dictionary<string, object?> activity = Activity();
        string preview = "Question 😀\n<at>not-authority</at> & \"quoted\"";
        Dictionary<string, object?>[] quotes = Enumerable.Range(0, 10)
            .Select(index => Quote("private-id-" + index, index == 0 ? "  Pául 😀  " : "unsafe\nname", index == 0 ? preview : "Question " + index)).ToArray();
        activity["entities"] = quotes;
        using HttpResponseMessage response = await Post(fixture, activity);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Message stored = await database.Entities.Load<Message>().ExecuteEntityAsync();
        Assert.That(stored.Body, Does.Contain(preview).And.Contain("Pául 😀"));
        Assert.That(stored.Body, Does.Contain("Question 7").And.Not.Contain("Question 8").And.Not.Contain("Question 9"));
        Assert.That(stored.Body, Does.Contain("Additional quoted messages omitted"));
        Assert.That(stored.Body, Does.Not.Contain("unsafe\nname").And.Not.Contain("private-quote-sender-canary").And.Not.Contain("private-id-"));
    }

    /// <summary>Checks the original dedup snapshot and reply target survive quote projection.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test, Parallelizable]
    public async Task TeamsIngress_QuotedMessage_RetainsOriginalReplyAndDedup() {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        using TeamsRoutingFixture fixture = new(database);
        Dictionary<string, object?> activity = Activity();
        activity["entities"] = new[] { Quote("quoted-old-id", "Paul", "first quoted snapshot") };
        using HttpResponseMessage response = await Post(fixture, activity);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Message stored = await database.Entities.Load<Message>().ExecuteEntityAsync();
        activity["entities"] = new[] { Quote("other-quoted-id", "Someone else", "changed duplicate quote") };
        using HttpResponseMessage replay = await Post(fixture, activity);
        Assert.That(replay.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Message original = await database.Entities.Load<Message>().ExecuteEntityAsync();
        Assert.That(original.Id, Is.EqualTo(stored.Id));
        Assert.That(original.Body, Is.EqualTo(stored.Body).And.Not.Contain("changed duplicate quote"));
        TeamsMessageOrigin origin = await database.Entities.Load<TeamsMessageOrigin>().ExecuteEntityAsync();
        Assert.That(origin.ActivityId, Is.EqualTo(activity["id"]));
        Assert.That(origin.ActivityId, Is.Not.EqualTo("quoted-old-id"));
        using HttpResponseMessage reply = await fixture.Reply(stored.Id);
        Assert.That(reply.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Dictionary<string, object> sent = Json.Read<Dictionary<string, object>>(fixture.Host.Connector.Bodies.Single());
        Assert.That(sent["replyToId"], Is.EqualTo(activity["id"]));
        Assert.That(sent["replyToId"], Is.Not.EqualTo("quoted-old-id"));
    }

    /// <summary>Checks the accepted UTF16 preview boundary is retained without truncation.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test, Parallelizable]
    public async Task TeamsIngress_QuoteAtSizeBoundary_RetainsExactPreview() {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        using TeamsRoutingFixture fixture = new(database);
        Dictionary<string, object?> activity = Activity();
        string preview = new string('x', 16382) + "😀";
        activity["entities"] = new[] { Quote("question-id", new string('a', 256), preview) };
        using HttpResponseMessage response = await Post(fixture, activity);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Message stored = await database.Entities.Load<Message>().ExecuteEntityAsync();
        Assert.That(stored.Body, Does.EndWith(preview).And.Contain(new string('a', 256)));
    }

    /// <summary>Checks malformed or unauthorized quote activities do not reach persistence.</summary>
    /// <param name="scenario">The rejected boundary input.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase("preview-type"), TestCase("unauthorized"), TestCase("channel"), Parallelizable]
    public async Task TeamsIngress_QuoteRejectedAtNativeBoundary_DoesNotPersist(string scenario) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        using TeamsRoutingFixture fixture = new(database);
        Dictionary<string, object?> activity = Activity();
        Dictionary<string, object?> quote = Quote("question-id", "Paul", "quote-boundary-canary");
        if (scenario == "preview-type")
            ((Dictionary<string, object?>)quote["quotedReply"]!)["preview"] = 17;
        if (scenario == "channel")
            activity["channelId"] = "not-teams";
        activity["entities"] = new[] { quote };
        using HttpResponseMessage response = await Post(fixture, activity, scenario == "unauthorized" ? "invalid-token" : null);
        Assert.That(response.StatusCode, Is.EqualTo(scenario == "unauthorized" ? HttpStatusCode.Unauthorized :
            scenario == "preview-type" ? HttpStatusCode.BadRequest : HttpStatusCode.Forbidden));
        Assert.That(await database.Entities.Load<Message>(DB.Count()).ExecuteScalarAsync<long>(), Is.Zero);
        Assert.That(await database.Entities.Load<TeamsMessageOrigin>(DB.Count()).ExecuteScalarAsync<long>(), Is.Zero);
        Assert.That(fixture.Host.Connector.Bodies, Is.Empty);
    }

    /// <summary>Checks the observed quoted-author placeholder is omitted without altering real labels or sender metadata.</summary>
    /// <param name="quotedName">The native quoted-author display label.</param>
    /// <param name="usable">Whether the label remains usable.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase("Display Name", false), TestCase("  display name  ", false), TestCase("DISPLAY NAME", false)]
    [TestCase("Display Name Jr.", true), TestCase("Display Names", true), TestCase("Dísplay Name 😀", true), Parallelizable]
    public async Task TeamsIngress_QuotedAuthorPlaceholder_OmitsOnlyObservedLabel(string quotedName, bool usable) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        using TeamsRoutingFixture fixture = new(database);
        Dictionary<string, object?> activity = Activity();
        activity["from"] = new Dictionary<string, object?> { ["id"] = "private-sender-canary", ["name"] = "Display Name" };
        activity["entities"] = new[] { Quote("question-id", quotedName, "quoted preview stays intact") };
        using HttpResponseMessage response = await Post(fixture, activity);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Message stored = await database.Entities.Load<Message>().ExecuteEntityAsync();
        Assert.That(stored.Body, Does.StartWith("External Microsoft Teams message (untrusted input):\n\n" + activity["text"]));
        Assert.That(stored.Body, Does.EndWith("quoted preview stays intact"));
        Assert.That(stored.ExternalAuthorDisplayName, Is.EqualTo("Display Name"));
        if (usable)
            Assert.That(stored.Body, Does.Contain("Quoted author display label (untrusted): " + quotedName));
        else
            Assert.That(stored.Body, Does.Not.Contain("Quoted author display label (untrusted):"));
        Assert.That(fixture.Host.Connector.Bodies, Is.Empty);
    }

    static Dictionary<string, object?> Activity() {
        Dictionary<string, object?> activity = TeamsWebhookFixture.Activity();
        activity["from"] = new Dictionary<string, object?> { ["id"] = "private-sender-canary", ["name"] = "Toni" };
        activity["text"] = "<quoted messageId=\"question-id\"/>\nCan you explain this?";
        return activity;
    }

    static Dictionary<string, object?> Quote(string id, string? name, string? preview) => new() {
        ["type"] = "quotedReply",
        ["quotedReply"] = new Dictionary<string, object?> { ["messageId"] = id, ["senderName"] = name, ["preview"] = preview, ["senderId"] = "private-quote-sender-canary" }
    };

    static Task<HttpResponseMessage> Post(TeamsRoutingFixture fixture, Dictionary<string, object?> activity, string? token = null) =>
        fixture.Host.Http.Post<Dictionary<string, object?>, HttpResponseMessage>(Rest.Path(TestSetup.BaseUrl, "api", "teams", "messages"), activity,
            new HttpOptions { TokenProvider = new NodeChangeTokenProvider(token ?? fixture.Host.Token()) });
}
