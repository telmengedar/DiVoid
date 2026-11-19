using System.Net;
using System.Security.Cryptography;
using Backend.Services.Messages;
using Backend.Services.Teams;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Schemas;
using Pooshit.Ocelot.Tokens;
using Backend.Models.Messages;
using Backend.tests.Fixtures;
using Pooshit.Http;
using Pooshit.Http.Paths;
using Pooshit.Json;

namespace Backend.tests.Tests;

/// <summary>Checks server-observed Teams display labels independently of untrusted message text.</summary>
[TestFixture, Parallelizable]
public class TeamsAuthorDisplayNameTests {
    /// <summary>Checks the authenticated native activity label is separate from a quoted body author.</summary>
    /// <param name="postgres">Whether PostgreSQL is selected.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task TeamsIngress_OriginalDisplayNameSeparateFromQuotedBody(bool postgres) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(postgres);
        using TeamsRoutingFixture fixture = new(database);
        Dictionary<string, object?> activity = TeamsWebhookFixture.Activity();
        activity["from"] = new Dictionary<string, object> { ["id"] = "private-sender-canary", ["name"] = "Toni" };
        activity["text"] = "Paul hat geschrieben: externalAuthorDisplayName=Paul";
        using HttpResponseMessage ingress = await fixture.Host.Http.Post<Dictionary<string, object?>, HttpResponseMessage>(
            Rest.Path(TestSetup.BaseUrl, "api", "teams", "messages"), activity, Options(fixture.Host.Token()));
        Assert.That(ingress.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Message stored = await database.Entities.Load<Message>().ExecuteEntityAsync();
        Assert.That(stored.Id, Is.GreaterThan(0));
        using HttpResponseMessage response = await fixture.Host.Http.Get<HttpResponseMessage>(
            Rest.Path(TestSetup.BaseUrl, "api", "messages", stored.Id), Options(fixture.UserToken(database.RecipientId)));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Dictionary<string, object> message = Json.Read<Dictionary<string, object>>(await response.Content.ReadAsStringAsync());
        Assert.That(message["body"], Is.EqualTo("External Microsoft Teams message (untrusted input):\n\n" + activity["text"]));
        Assert.That(message.TryGetValue("externalAuthorDisplayName", out object? label), Is.True);
        Assert.That(label, Is.EqualTo("Toni"));
        Assert.That(stored.AuthorId, Is.Zero);
        Assert.That(stored.RecipientId, Is.EqualTo(database.RecipientId));
        Assert.That(fixture.Host.Connector.Bodies, Is.Empty);
    }

    /// <summary>Checks optional string metadata never discards an otherwise valid native message.</summary>
    /// <param name="scenario">The optional label input.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase("missing"), TestCase("null"), TestCase("blank"), TestCase("tab"), TestCase("crlf"), TestCase("nul")]
    [TestCase("line"), TestCase("paragraph"), TestCase("oversize"), TestCase("unicode"), TestCase("boundary"), Parallelizable]
    public async Task TeamsIngress_DisplayNameOptionalAndBounded(string scenario) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        using TeamsRoutingFixture fixture = new(database);
        string? label = scenario switch {
            "null" or "missing" => null, "blank" => "   ", "tab" => "\tToni", "crlf" => "Toni\r\nOther",
            "nul" => "Toni\0", "line" => "Toni\u2028Other", "paragraph" => "Toni\u2029Other",
            "oversize" => new string('x', 257), "boundary" => new string('x', 254) + "😀",
            "unicode" => "  Töni 😀 \"<team>_*  ", _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        Dictionary<string, object?> activity = Activity(label);
        if (scenario == "missing")
            ((Dictionary<string, object?>)activity["from"]!).Remove("name");
        long id = await Receive(fixture, activity);
        Message stored = await database.Entities.Load<Message>().Where(row => row.Id == id).ExecuteEntityAsync();
        Assert.That(stored.Body, Does.Contain("Paul hat geschrieben"));
        string? expected = scenario is "unicode" or "boundary" ? label!.Trim() : null;
        Assert.That(stored.ExternalAuthorDisplayName, Is.EqualTo(expected));
        using HttpResponseMessage response = await Get(fixture, id);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        MessageDetails details = Json.Read<MessageDetails>(await response.Content.ReadAsStringAsync());
        Assert.That(details.ExternalAuthorDisplayName, Is.EqualTo(expected));
        Assert.That(fixture.Host.Connector.Bodies, Is.Empty);
    }

    /// <summary>Checks malformed SDK name types retain the current structural rejection boundary.</summary>
    /// <param name="kind">The invalid JSON value.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase("number"), TestCase("object"), TestCase("array"), TestCase("bool"), TestCase("surrogate"), Parallelizable]
    public async Task TeamsIngress_MalformedNameJson_PreservesBadRequest(string kind) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        using TeamsRoutingFixture fixture = new(database);
        Dictionary<string, object?> activity = Activity("replace-label-canary");
        string json = Json.WriteString(activity);
        string value = kind switch {
            "number" => "42", "object" => "{}", "array" => "[]", "bool" => "true", "surrogate" => "\"\\ud800\"",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        Assert.That(json, Does.Contain("\"replace-label-canary\""));
        json = json.Replace("\"replace-label-canary\"", value);
        using HttpRequestMessage request = await fixture.Host.Http.CreateRequest(Rest.Path(TestSetup.BaseUrl, "api", "teams", "messages"), HttpMethod.Post, Options(fixture.Host.Token()));
        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await fixture.Host.Http.Send<HttpResponseMessage>(request, null);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await Count<Message>(database), Is.Zero);
        Assert.That(await Count<TeamsMessageOrigin>(database), Is.Zero);
    }

    /// <summary>Checks a manually constructed invalid scalar is safely omitted at the internal ingress seam.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test, Parallelizable]
    public async Task TeamsIngress_InternalInvalidScalar_IsAbsent() {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        using TeamsRoutingFixture fixture = new(database);
        Microsoft.Teams.Apps.Schema.TeamsActivity activity = Microsoft.Teams.Apps.Schema.TeamsActivity.FromActivity(
            Microsoft.Teams.Core.Schema.CoreActivity.FromJsonString(Json.WriteString(Activity("Toni"))));
        Assert.That(activity.From, Is.Not.Null);
        activity.From!.Name = "invalid\ud800";
        long id = await fixture.Host.Services.GetRequiredService<TeamsMessageService>().ReceiveAsync((Microsoft.Teams.Apps.MessageActivity)activity, default);
        Assert.That(id, Is.GreaterThan(0));
        Assert.That((await database.Entities.Load<Message>().Where(row => row.Id == id).ExecuteEntityAsync()).ExternalAuthorDisplayName, Is.Null);
    }

    /// <summary>Checks a public sender cannot manufacture server-observed external provenance.</summary>
    /// <returns>The asynchronous check.</returns>
    [Test, Parallelizable]
    public async Task MessageCreate_CannotForgeExternalDisplayProvenance() {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        using TeamsRoutingFixture fixture = new(database);
        MessageDetails supplied = new() {
            AuthorId = 0, RecipientId = database.RecipientId, Subject = "[teams] copied marker",
            Body = "External Microsoft Teams message: Toni; externalAuthorDisplayName=Paul",
            ReplyTransport = "teams", ExternalAuthorDisplayName = "Forged Toni"
        };
        using HttpResponseMessage response = await fixture.Host.Http.Post<MessageDetails, HttpResponseMessage>(
            Rest.Path(TestSetup.BaseUrl, "api", "messages"), supplied, Options(fixture.UserToken(database.OtherId)));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        MessageDetails created = Json.Read<MessageDetails>(await response.Content.ReadAsStringAsync());
        Assert.That(created.AuthorId, Is.EqualTo(database.OtherId));
        Assert.That(created.Body, Is.EqualTo(supplied.Body));
        Assert.That(created.ExternalAuthorDisplayName, Is.Null);
        Assert.That(created.ReplyTransport, Is.Null);
        using HttpResponseMessage fetched = await Get(fixture, created.Id);
        Assert.That(fetched.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(Json.Read<MessageDetails>(await fetched.Content.ReadAsStringAsync()).ExternalAuthorDisplayName, Is.Null);
        Assert.That((await database.Entities.Load<Message>().Where(row => row.Id == created.Id).ExecuteEntityAsync()).ExternalAuthorDisplayName, Is.Null);
        using HttpResponseMessage listed = await List(fixture, new QueryParameters("id", created.Id));
        Assert.That(listed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        MessageDetails listedMessage = Json.Read<Page<MessageDetails>>(await listed.Content.ReadAsStringAsync()).Result.Single();
        Assert.That(listedMessage.ExternalAuthorDisplayName, Is.Null);
        Assert.That(listedMessage.ReplyTransport, Is.Null);
        Assert.That(await Count<TeamsMessageOrigin>(database), Is.Zero);
    }

    /// <summary>Checks default and explicit projections share existing privacy scopes and string serialization.</summary>
    /// <param name="postgres">Whether PostgreSQL is selected.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task MessageProjection_ExternalDisplayName_DefaultExplicitAndIsolation(bool postgres) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(postgres);
        using TeamsRoutingFixture fixture = new(database);
        string label = "Töni \"<b>😀</b>_*";
        long id = await Receive(fixture, Activity(label));
        foreach (bool explicitFields in new[] { false, true }) {
            QueryParameters query = new("id", id);
            if (explicitFields)
                query.Add("fields", new[] { "id", "body", "replytransport", "externalauthordisplayname" });
            using HttpResponseMessage response = await List(fixture, query);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            string json = await response.Content.ReadAsStringAsync();
            Page<MessageDetails> page = Json.Read<Page<MessageDetails>>(json);
            Assert.That(page.Result.Single().ExternalAuthorDisplayName, Is.EqualTo(label));
            if (explicitFields)
                Assert.That(page.Result.Single().Body, Does.Contain("Paul hat geschrieben"));
            Assert.That(json, Does.Not.Contain("private-sender-canary").And.Not.Contain(TeamsWebhookFixture.ServiceUrl).And.Not.Contain("conversation-1"));
        }
        long unrelated = await database.AddUserAsync(true, ["read"]);
        using HttpResponseMessage invisible = await Get(fixture, id, unrelated);
        Assert.That(invisible.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        QueryParameters privateQuery = new("recipientid", database.RecipientId);
        privateQuery.Add("fields", new[] { "id", "externalauthordisplayname" });
        using HttpResponseMessage hidden = await List(fixture, privateQuery, unrelated);
        Assert.That(hidden.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(Json.Read<Page<MessageDetails>>(await hidden.Content.ReadAsStringAsync()).Result, Is.Empty);
        using HttpResponseMessage admin = await Get(fixture, id, database.OtherId);
        Assert.That(admin.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(Json.Read<MessageDetails>(await admin.Content.ReadAsStringAsync()).ExternalAuthorDisplayName, Is.EqualTo(label));
        using HttpResponseMessage consumed = await fixture.Host.Http.Delete<HttpResponseMessage>(Rest.Path(TestSetup.BaseUrl, "api", "messages", id), Options(fixture.UserToken(database.RecipientId)));
        Assert.That(consumed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await Count<Message>(database), Is.Zero);
        TeamsMessageOrigin retained = await database.Entities.Load<TeamsMessageOrigin>().ExecuteEntityAsync();
        Assert.That(retained.ConsumedAt, Is.Not.Null);
        Assert.That(Json.WriteString(retained), Does.Not.Contain(label).And.Not.Contain("externalAuthorDisplayName"));
    }

    /// <summary>Checks concurrent duplicates and later rebind/replay preserve the first committed snapshot.</summary>
    /// <param name="postgres">Whether PostgreSQL is selected.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task TeamsIngress_DuplicateDisplayName_FirstCommittedSnapshotWins(bool postgres) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(postgres);
        using TeamsRoutingFixture fixture = new(database);
        Dictionary<string, object?> first = Activity("Toni");
        first["text"] = "first Toni body";
        Dictionary<string, object?> second = Activity("Paul");
        second["text"] = "second Paul body";
        HttpResponseMessage[] responses = await Task.WhenAll(Post(fixture, first), Post(fixture, second));
        foreach (HttpResponseMessage response in responses) {
            using (response)
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
        Message original = await database.Entities.Load<Message>().ExecuteEntityAsync();
        Assert.That(original.ExternalAuthorDisplayName, Is.AnyOf("Toni", "Paul"));
        Assert.That(original.Body, Does.Contain(original.ExternalAuthorDisplayName == "Toni" ? "first Toni body" : "second Paul body"));
        string originBefore = Json.WriteString(await database.Entities.Load<TeamsMessageOrigin>().ExecuteEntityAsync());
        await database.SetBindingAsync(database.BindingId, database.RecipientId, database.OtherId, "rebound");
        using HttpResponseMessage replay = await Post(fixture, Activity("Replacement name"));
        Assert.That(replay.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await Count<Message>(database), Is.EqualTo(1));
        Assert.That(await Count<TeamsMessageOrigin>(database), Is.EqualTo(1));
        Message after = await database.Entities.Load<Message>().ExecuteEntityAsync();
        Assert.That(after.Id, Is.EqualTo(original.Id));
        Assert.That(after.ExternalAuthorDisplayName, Is.EqualTo(original.ExternalAuthorDisplayName));
        Assert.That(after.Body, Is.EqualTo(original.Body));
        Assert.That(after.RecipientId, Is.EqualTo(database.RecipientId));
        Assert.That(Json.WriteString(await database.Entities.Load<TeamsMessageOrigin>().ExecuteEntityAsync()), Is.EqualTo(originBefore));
        Assert.That(fixture.Host.Connector.Bodies, Is.Empty);
    }

    /// <summary>Checks real provider nullable-column upgrades preserve pre-existing message and origin rows.</summary>
    /// <param name="postgres">Whether PostgreSQL is selected.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task MessageSchema_AdditiveExternalDisplayName_PreservesLegacyRows(bool postgres) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(postgres);
        using TeamsRoutingFixture fixture = new(database);
        long teamsId = await Receive(fixture, Activity("old dropped metadata"));
        MessageDetails ordinary = await fixture.Host.Services.GetRequiredService<MessageService>().Create(database.OtherId,
            new MessageDetails { RecipientId = database.RecipientId, Subject = "legacy ordinary", Body = "legacy body unchanged" });
        await database.Entities.Update<TeamsMessageOrigin>().Set(row => row.ReplyState == TeamsReplyState.Unknown, row => row.ReplyBodyHash == "unchanged-private-hash").ExecuteAsync();
        Message teamsBefore = await database.Entities.Load<Message>().Where(row => row.Id == teamsId).ExecuteEntityAsync();
        Message ordinaryBefore = await database.Entities.Load<Message>().Where(row => row.Id == ordinary.Id).ExecuteEntityAsync();
        string originBefore = Json.WriteString(await database.Entities.Load<TeamsMessageOrigin>().ExecuteEntityAsync());
        await database.Entities.DBClient.NonQueryAsync("ALTER TABLE message DROP COLUMN externalauthordisplayname");
        using (Transaction transaction = database.Entities.Transaction()) {
            await new SchemaService(database.Entities.DBClient).CreateOrUpdateSchema<Message>(transaction);
            transaction.Commit();
        }
        List<Message> rows = [];
        await foreach (Message row in database.Entities.Load<Message>().ExecuteEntitiesAsync())
            rows.Add(row);
        Assert.That(rows.Select(row => row.Id), Is.EquivalentTo(new[] { teamsId, ordinary.Id }));
        Assert.That(rows.All(row => row.ExternalAuthorDisplayName == null), Is.True);
        teamsBefore.ExternalAuthorDisplayName = null;
        Assert.That(Json.WriteString(rows.Single(row => row.Id == teamsId)), Is.EqualTo(Json.WriteString(teamsBefore)));
        Assert.That(Json.WriteString(rows.Single(row => row.Id == ordinary.Id)), Is.EqualTo(Json.WriteString(ordinaryBefore)));
        Assert.That(Json.WriteString(await database.Entities.Load<TeamsMessageOrigin>().ExecuteEntityAsync()), Is.EqualTo(originBefore));
        Dictionary<string, object?> fresh = Activity("new available label");
        fresh["id"] = "new-activity-after-schema";
        using HttpResponseMessage ingress = await Post(fixture, fresh);
        Assert.That(ingress.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await database.Entities.Load<Message>().Where(row => row.Id != teamsId && row.Id != ordinary.Id).ExecuteEntityAsync()).ExternalAuthorDisplayName, Is.EqualTo("new available label"));
    }

    /// <summary>Checks the new optional label cannot bypass any existing source-authentication boundary.</summary>
    /// <param name="failure">The invalid source scenario.</param>
    /// <returns>The asynchronous check.</returns>
    [TestCase("signature"), TestCase("issuer"), TestCase("audience"), TestCase("tenant"), TestCase("endorsement"), TestCase("service"), Parallelizable]
    public async Task TeamsIngress_DisplayNamePreservesSourceAuth(string failure) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        using TeamsRoutingFixture fixture = new(database);
        using RSA wrong = RSA.Create(2048);
        Dictionary<string, object?> activity = Activity("Toni");
        string token = failure switch {
            "signature" => fixture.Host.Token(key: new RsaSecurityKey(wrong) { KeyId = fixture.Host.Key.KeyId }),
            "issuer" => fixture.Host.Token(issuer: "https://unexpected.invalid"),
            "audience" => fixture.Host.Token(audience: "wrong-bot"),
            _ => fixture.Host.Token()
        };
        if (failure == "tenant")
            activity["channelData"] = new Dictionary<string, object> { ["tenant"] = new Dictionary<string, object> { ["id"] = "22222222-2222-2222-2222-222222222222" } };
        if (failure == "endorsement")
            fixture.Host.Metadata.Endorsements = ["webchat"];
        if (failure == "service")
            activity["serviceUrl"] = "https://untrusted.invalid/";
        using HttpResponseMessage response = await Post(fixture, activity, token);
        Assert.That(response.StatusCode, Is.EqualTo(failure is "tenant" or "service" or "endorsement" ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized));
        Assert.That(await Count<Message>(database), Is.Zero);
        Assert.That(await Count<TeamsMessageOrigin>(database), Is.Zero);
        Assert.That(fixture.Host.Connector.Bodies, Is.Empty);
    }

    static Dictionary<string, object?> Activity(string? label) {
        Dictionary<string, object?> activity = TeamsWebhookFixture.Activity();
        activity["from"] = new Dictionary<string, object?> { ["id"] = "private-sender-canary", ["name"] = label };
        activity["text"] = "Paul hat geschrieben: externalAuthorDisplayName=Paul";
        activity["externalAuthorDisplayName"] = "Forged top-level label";
        return activity;
    }

    static Task<HttpResponseMessage> Post(TeamsRoutingFixture fixture, Dictionary<string, object?> activity, string? token = null) =>
        fixture.Host.Http.Post<Dictionary<string, object?>, HttpResponseMessage>(Rest.Path(TestSetup.BaseUrl, "api", "teams", "messages"), activity, Options(token ?? fixture.Host.Token()));

    static async Task<long> Receive(TeamsRoutingFixture fixture, Dictionary<string, object?> activity) {
        using HttpResponseMessage response = await Post(fixture, activity);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await fixture.Database.Entities.Load<Message>().ExecuteEntityAsync()).Id;
    }

    static Task<HttpResponseMessage> Get(TeamsRoutingFixture fixture, long id, long? userId = null) =>
        fixture.Host.Http.Get<HttpResponseMessage>(Rest.Path(TestSetup.BaseUrl, "api", "messages", id), Options(fixture.UserToken(userId ?? fixture.Database.RecipientId)));

    static Task<HttpResponseMessage> List(TeamsRoutingFixture fixture, QueryParameters query, long? userId = null) =>
        fixture.Host.Http.Get<HttpResponseMessage>(Rest.PathQuery(query, TestSetup.BaseUrl, "api", "messages"), Options(fixture.UserToken(userId ?? fixture.Database.RecipientId)));

    static Task<long> Count<T>(TeamsRoutingDatabase database) => database.Entities.Load<T>(DB.Count()).ExecuteScalarAsync<long>();

    static HttpOptions Options(string token) => new() { TokenProvider = new NodeChangeTokenProvider(token) };
}
