#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Backend.Models.Messages;
using Backend.Models.Users;
using Backend.tests.Fixtures;
using NUnit.Framework;
using Pooshit.Json;
using Pooshit.Ocelot.Entities;

namespace Backend.tests.Tests;

/// <summary>
/// HTTP integration tests for the messaging system (task #425).
///
/// Covers the 15 load-bearing cases from Sarah's architectural doc §15.1:
///   T1  — round-trip: POST creates and re-reads row correctly
///   T2  — authorId in body is ignored; server forces callerId as author (anti-impersonation)
///   T3  — POST to unknown recipient returns 404
///   T4a — POST with empty subject returns 400
///   T4b — POST with oversize subject returns 400
///   T5  — non-admin list scope: caller sees only own messages (author OR recipient)
///   T6  — non-admin with ?recipientId= of another user returns empty (scope AND filter)
///   T7  — admin sees all messages regardless of involvement
///   T8  — default sort is createdat DESC (newest first)
///   T9  — GET /{id} returns 404 to non-related caller (not 403 — existence must not leak via status code)
///   T10 — sender cannot DELETE their own sent message (recipient-or-admin only)
///   T11 — DELETE by recipient succeeds; second DELETE returns 404 (idempotency)
///   T12 — admin can DELETE any message
///   T13 — PATCH returns 404 or 405 (patch surface is structurally absent)
///   T14 — subject is trimmed of leading/trailing whitespace server-side
/// </summary>
[TestFixture]
public class MessageHttpTests
{
    JwtAuthFixture fixture = null!;
    IEntityManager db = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        fixture = new JwtAuthFixture();
        db      = fixture.EntityManager;
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        fixture.Dispose();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    async Task<long> InsertUserAsync(string name, string? permissionsJson = null)
    {
        return await db.Insert<User>()
                       .Columns(u => u.Name, u => u.Email, u => u.Enabled, u => u.Permissions, u => u.CreatedAt)
                       .Values(name, $"{name}@test.com", true, permissionsJson, DateTime.UtcNow)
                       .ReturnID()
                       .ExecuteAsync();
    }

    HttpClient ClientWithToken(string token)
    {
        HttpClient client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    static StringContent JsonBody(object body)
    {
        // Pooshit.Json does not serialize anonymous types — use System.Text.Json instead.
        string json = JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return new StringContent(json, System.Text.Encoding.UTF8, "application/json");
    }

    static async Task<MessageDetails> ReadMessageAsync(HttpResponseMessage response)
    {
        string json = await response.Content.ReadAsStringAsync();
        return Json.Read<MessageDetails>(json)
               ?? throw new InvalidOperationException($"Failed to deserialize MessageDetails: {json}");
    }

    static async Task<MessageDetails[]> ReadMessagesAsync(HttpResponseMessage response)
    {
        string json = await response.Content.ReadAsStringAsync();
        // The list endpoint streams a page envelope: { result: [...], count: N, continue: X }
        // Use System.Text.Json with case-insensitive matching to parse the result array.
        if (string.IsNullOrEmpty(json)) return [];
        JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        JsonElement items;
        if (!root.TryGetProperty("result", out items) && !root.TryGetProperty("items", out items))
            return [];
        if (items.ValueKind != JsonValueKind.Array) return [];
        JsonSerializerOptions options = new() { PropertyNameCaseInsensitive = true };
        return items.Deserialize<MessageDetails[]>(options) ?? [];
    }

    // -----------------------------------------------------------------------
    // T1 — round-trip POST → GET returns persisted row
    // -----------------------------------------------------------------------

    [Test]
    public async Task T1_Create_WithValidPayload_PersistsAndReturnsRow()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long authorId  = await InsertUserAsync($"t1-author-{suffix}", Json.WriteString(new[] { "write" }));
        long recipientId = await InsertUserAsync($"t1-recipient-{suffix}", Json.WriteString(new[] { "read" }));

        string authorToken = fixture.MintToken(userId: authorId);
        HttpClient client = ClientWithToken(authorToken);

        HttpResponseMessage postResponse = await client.PostAsync("/api/messages", JsonBody(new {
            recipientId = recipientId,
            subject     = "hello T1",
            body        = "body of T1 message"
        }));

        Assert.That((int)postResponse.StatusCode, Is.EqualTo(200),
            "POST /api/messages must return 200 for a valid payload");

        MessageDetails created = await ReadMessageAsync(postResponse);
        Assert.Multiple(() => {
            Assert.That(created.Id, Is.GreaterThan(0),
                "T1: server must assign a positive Id");
            Assert.That(created.AuthorId, Is.EqualTo(authorId),
                "T1: AuthorId must match the authenticated caller");
            Assert.That(created.RecipientId, Is.EqualTo(recipientId),
                "T1: RecipientId must match the supplied value");
            Assert.That(created.Subject, Is.EqualTo("hello T1"),
                "T1: Subject must round-trip unchanged");
            Assert.That(created.Body, Is.EqualTo("body of T1 message"),
                "T1: Body must round-trip unchanged");
            Assert.That(created.CreatedAt, Is.Not.EqualTo(default(DateTime)),
                "T1: CreatedAt must be set server-side");
        });

        // Verify the row was actually written to the DB, not just echoed.
        string recipientToken = fixture.MintToken(userId: recipientId);
        HttpClient recipientClient = ClientWithToken(recipientToken);
        HttpResponseMessage getResponse = await recipientClient.GetAsync($"/api/messages/{created.Id}");
        Assert.That((int)getResponse.StatusCode, Is.EqualTo(200),
            "T1: GET /{id} after POST must return 200, confirming the row was persisted");

        MessageDetails fetched = await ReadMessageAsync(getResponse);
        Assert.That(fetched.Subject, Is.EqualTo("hello T1"),
            "T1: fetched Subject must match what was POSTed");
    }

    // -----------------------------------------------------------------------
    // T2 — authorId in body is ignored; server forces callerId as author
    // -----------------------------------------------------------------------

    [Test]
    public async Task T2_Create_AuthorIdInBodyIsIgnored_ServerForcesCallerAsAuthor()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long realAuthorId   = await InsertUserAsync($"t2-real-{suffix}", Json.WriteString(new[] { "write" }));
        long spoofAuthorId  = await InsertUserAsync($"t2-spoof-{suffix}", Json.WriteString(new[] { "write" }));
        long recipientId    = await InsertUserAsync($"t2-recipient-{suffix}", Json.WriteString(new[] { "read" }));

        string token = fixture.MintToken(userId: realAuthorId);
        HttpClient client = ClientWithToken(token);

        // Supply a different authorId in the body — the server must ignore it.
        HttpResponseMessage postResponse = await client.PostAsync("/api/messages", JsonBody(new {
            authorId    = spoofAuthorId,
            recipientId = recipientId,
            subject     = "T2 impersonation attempt",
            body        = "body"
        }));

        Assert.That((int)postResponse.StatusCode, Is.EqualTo(200),
            "T2: POST must succeed for an authenticated caller with write permission");

        MessageDetails created = await ReadMessageAsync(postResponse);
        Assert.That(created.AuthorId, Is.EqualTo(realAuthorId),
            "T2 (CRITICAL): AuthorId in the response must be the authenticated callerId, " +
            "not the spoofAuthorId supplied in the request body. " +
            "A failure here means impersonation via POST body is possible.");

        // Confirm at the DB level by fetching the row directly.
        Message? dbRow = await db.Load<Message>()
                                 .Where(m => m.Id == created.Id)
                                 .ExecuteEntityAsync();
        Assert.That(dbRow, Is.Not.Null, "T2: row must exist in DB");
        Assert.That(dbRow!.AuthorId, Is.EqualTo(realAuthorId),
            "T2 (CRITICAL): DB-level AuthorId must be the authenticated callerId, not the spoofed value");
    }

    // -----------------------------------------------------------------------
    // T3 — POST to unknown recipient returns 404
    // -----------------------------------------------------------------------

    [Test]
    public async Task T3_Create_UnknownRecipient_Returns404()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long authorId = await InsertUserAsync($"t3-author-{suffix}", Json.WriteString(new[] { "write" }));

        string token = fixture.MintToken(userId: authorId);
        HttpClient client = ClientWithToken(token);

        HttpResponseMessage response = await client.PostAsync("/api/messages", JsonBody(new {
            recipientId = 999999999L,
            subject     = "T3 unknown recipient",
            body        = "body"
        }));

        Assert.That((int)response.StatusCode, Is.EqualTo(404),
            "T3: POST to a non-existent recipientId must return 404. " +
            "A failure here means messages can be addressed to ghost users.");
    }

    // -----------------------------------------------------------------------
    // T4a — empty subject returns 400
    // -----------------------------------------------------------------------

    [Test]
    public async Task T4a_Create_EmptySubject_Returns400()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long authorId    = await InsertUserAsync($"t4a-author-{suffix}", Json.WriteString(new[] { "write" }));
        long recipientId = await InsertUserAsync($"t4a-recipient-{suffix}", Json.WriteString(new[] { "read" }));

        string token = fixture.MintToken(userId: authorId);
        HttpClient client = ClientWithToken(token);

        HttpResponseMessage response = await client.PostAsync("/api/messages", JsonBody(new {
            recipientId = recipientId,
            subject     = "",
            body        = "body"
        }));

        Assert.That((int)response.StatusCode, Is.EqualTo(400),
            "T4a: empty subject must return 400. " +
            "A failure here means the empty-subject validation is missing.");
    }

    // -----------------------------------------------------------------------
    // T4b — subject over 256 chars returns 400
    // -----------------------------------------------------------------------

    [Test]
    public async Task T4b_Create_OversizeSubject_Returns400()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long authorId    = await InsertUserAsync($"t4b-author-{suffix}", Json.WriteString(new[] { "write" }));
        long recipientId = await InsertUserAsync($"t4b-recipient-{suffix}", Json.WriteString(new[] { "read" }));

        string token = fixture.MintToken(userId: authorId);
        HttpClient client = ClientWithToken(token);

        string longSubject = new string('x', 257);
        HttpResponseMessage response = await client.PostAsync("/api/messages", JsonBody(new {
            recipientId = recipientId,
            subject     = longSubject,
            body        = "body"
        }));

        Assert.That((int)response.StatusCode, Is.EqualTo(400),
            "T4b: a 257-char subject must return 400, not 500. " +
            "A failure here means [Size(256)] alone is used without service-side validation, " +
            "producing a DB error instead of a clean 400.");
    }

    // -----------------------------------------------------------------------
    // T5 — non-admin sees only messages where they are author or recipient
    // -----------------------------------------------------------------------

    [Test]
    public async Task T5_List_NonAdmin_OnlySeesOwnMessages()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long userA = await InsertUserAsync($"t5-A-{suffix}", Json.WriteString(new[] { "write" }));
        long userB = await InsertUserAsync($"t5-B-{suffix}", Json.WriteString(new[] { "write" }));
        long userC = await InsertUserAsync($"t5-C-{suffix}", Json.WriteString(new[] { "read" }));

        // Insert messages directly so we control the authorId/recipientId precisely.
        DateTime now = DateTime.UtcNow;
        long msgAtoB = await db.Insert<Message>()
                               .Columns(m => m.AuthorId, m => m.RecipientId, m => m.Subject, m => m.Body, m => m.CreatedAt)
                               .Values(userA, userB, $"T5-A->B-{suffix}", "body", now)
                               .ReturnID()
                               .ExecuteAsync();
        long msgBtoA = await db.Insert<Message>()
                               .Columns(m => m.AuthorId, m => m.RecipientId, m => m.Subject, m => m.Body, m => m.CreatedAt)
                               .Values(userB, userA, $"T5-B->A-{suffix}", "body", now.AddSeconds(1))
                               .ReturnID()
                               .ExecuteAsync();
        long msgBtoC = await db.Insert<Message>()
                               .Columns(m => m.AuthorId, m => m.RecipientId, m => m.Subject, m => m.Body, m => m.CreatedAt)
                               .Values(userB, userC, $"T5-B->C-{suffix}", "body", now.AddSeconds(2))
                               .ReturnID()
                               .ExecuteAsync();

        // Caller A: must see A→B and B→A only (not B→C).
        string tokenA = fixture.MintToken(userId: userA);
        HttpClient clientA = ClientWithToken(tokenA);
        HttpResponseMessage listResponseA = await clientA.GetAsync("/api/messages");
        Assert.That((int)listResponseA.StatusCode, Is.EqualTo(200));
        MessageDetails[] aMessages = await ReadMessagesAsync(listResponseA);

        long[] aIds = Array.ConvertAll(aMessages, m => m.Id);
        Assert.Multiple(() => {
            Assert.That(aIds, Does.Contain(msgAtoB),
                "T5: caller A must see the A→B message (A is author)");
            Assert.That(aIds, Does.Contain(msgBtoA),
                "T5: caller A must see the B→A message (A is recipient)");
            Assert.That(aIds, Does.Not.Contain(msgBtoC),
                "T5 (CRITICAL): caller A must NOT see B→C message where A is neither author nor recipient. " +
                "A failure here means the principal-scoping clause is missing or wrong.");
        });

        // Caller B: sees all three messages.
        string tokenB = fixture.MintToken(userId: userB);
        HttpClient clientB = ClientWithToken(tokenB);
        HttpResponseMessage listResponseB = await clientB.GetAsync("/api/messages");
        Assert.That((int)listResponseB.StatusCode, Is.EqualTo(200));
        MessageDetails[] bMessages = await ReadMessagesAsync(listResponseB);
        long[] bIds = Array.ConvertAll(bMessages, m => m.Id);

        Assert.Multiple(() => {
            Assert.That(bIds, Does.Contain(msgAtoB), "T5: B sees A→B (recipient)");
            Assert.That(bIds, Does.Contain(msgBtoA), "T5: B sees B→A (author)");
            Assert.That(bIds, Does.Contain(msgBtoC), "T5: B sees B→C (author)");
        });
    }

    // -----------------------------------------------------------------------
    // T6 — non-admin with ?recipientId= of another user returns empty
    // -----------------------------------------------------------------------

    [Test]
    public async Task T6_List_NonAdmin_WithRecipientIdOfAnother_ReturnsEmpty()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long userA = await InsertUserAsync($"t6-A-{suffix}", Json.WriteString(new[] { "write" }));
        long userB = await InsertUserAsync($"t6-B-{suffix}", Json.WriteString(new[] { "write" }));
        long userC = await InsertUserAsync($"t6-C-{suffix}", Json.WriteString(new[] { "write" }));

        // Seed: B→C message; A is not involved.
        DateTime now = DateTime.UtcNow;
        await db.Insert<Message>()
                .Columns(m => m.AuthorId, m => m.RecipientId, m => m.Subject, m => m.Body, m => m.CreatedAt)
                .Values(userB, userC, $"T6-B->C-{suffix}", "body", now)
                .ExecuteAsync();

        // Caller A asks for recipientId=C — but A has no messages involving A,
        // so the scoping clause (AuthorId==A OR RecipientId==A) ANDed with RecipientId==C is empty.
        string tokenA = fixture.MintToken(userId: userA);
        HttpClient clientA = ClientWithToken(tokenA);
        HttpResponseMessage response = await clientA.GetAsync($"/api/messages?recipientId={userC}");
        Assert.That((int)response.StatusCode, Is.EqualTo(200));

        MessageDetails[] messages = await ReadMessagesAsync(response);
        Assert.That(messages, Is.Empty,
            "T6 (CRITICAL): non-admin caller A with ?recipientId=C must get an empty result, " +
            "not C's messages. A failure here means the caller-supplied filter replaces the " +
            "principal-scoping clause instead of being ANDed on top of it.");
    }

    // -----------------------------------------------------------------------
    // T7 — admin sees all messages
    // -----------------------------------------------------------------------

    [Test]
    public async Task T7_List_Admin_SeesAllMessages()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long adminId  = await InsertUserAsync($"t7-admin-{suffix}", Json.WriteString(new[] { "admin" }));
        long userA    = await InsertUserAsync($"t7-A-{suffix}", Json.WriteString(new[] { "write" }));
        long userB    = await InsertUserAsync($"t7-B-{suffix}", Json.WriteString(new[] { "read" }));

        DateTime now = DateTime.UtcNow;
        long msg1 = await db.Insert<Message>()
                            .Columns(m => m.AuthorId, m => m.RecipientId, m => m.Subject, m => m.Body, m => m.CreatedAt)
                            .Values(userA, userB, $"T7-msg1-{suffix}", "body", now)
                            .ReturnID()
                            .ExecuteAsync();
        long msg2 = await db.Insert<Message>()
                            .Columns(m => m.AuthorId, m => m.RecipientId, m => m.Subject, m => m.Body, m => m.CreatedAt)
                            .Values(userB, userA, $"T7-msg2-{suffix}", "body", now.AddSeconds(1))
                            .ReturnID()
                            .ExecuteAsync();

        string adminToken = fixture.MintToken(userId: adminId);
        HttpClient adminClient = ClientWithToken(adminToken);
        HttpResponseMessage response = await adminClient.GetAsync("/api/messages");
        Assert.That((int)response.StatusCode, Is.EqualTo(200));

        MessageDetails[] messages = await ReadMessagesAsync(response);
        long[] ids = Array.ConvertAll(messages, m => m.Id);

        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(msg1),
                "T7: admin must see msg1 (A→B, admin is neither)");
            Assert.That(ids, Does.Contain(msg2),
                "T7: admin must see msg2 (B→A, admin is neither). " +
                "A failure here means the admin bypass of the scoping clause is broken.");
        });
    }

    // -----------------------------------------------------------------------
    // T8 — default sort is createdat DESC (newest first)
    // -----------------------------------------------------------------------

    [Test]
    public async Task T8_List_SortByCreatedAtDescending_NewestFirst()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long userA = await InsertUserAsync($"t8-A-{suffix}", Json.WriteString(new[] { "write" }));
        long userB = await InsertUserAsync($"t8-B-{suffix}", Json.WriteString(new[] { "read" }));

        // Insert three messages with increasing CreatedAt values.
        DateTime base_ = DateTime.UtcNow;
        long older  = await db.Insert<Message>()
                              .Columns(m => m.AuthorId, m => m.RecipientId, m => m.Subject, m => m.Body, m => m.CreatedAt)
                              .Values(userA, userB, $"T8-older-{suffix}", "body", base_.AddSeconds(-10))
                              .ReturnID()
                              .ExecuteAsync();
        long middle = await db.Insert<Message>()
                              .Columns(m => m.AuthorId, m => m.RecipientId, m => m.Subject, m => m.Body, m => m.CreatedAt)
                              .Values(userA, userB, $"T8-middle-{suffix}", "body", base_.AddSeconds(-5))
                              .ReturnID()
                              .ExecuteAsync();
        long newest = await db.Insert<Message>()
                              .Columns(m => m.AuthorId, m => m.RecipientId, m => m.Subject, m => m.Body, m => m.CreatedAt)
                              .Values(userA, userB, $"T8-newest-{suffix}", "body", base_)
                              .ReturnID()
                              .ExecuteAsync();

        // Caller A sees these messages; no ?sort= supplied — default should be DESC.
        string tokenA = fixture.MintToken(userId: userA);
        HttpClient clientA = ClientWithToken(tokenA);
        HttpResponseMessage response = await clientA.GetAsync("/api/messages");
        Assert.That((int)response.StatusCode, Is.EqualTo(200));

        MessageDetails[] messages = await ReadMessagesAsync(response);

        // Find our three seeded messages in the returned list.
        int idxNewest = -1, idxMiddle = -1, idxOlder = -1;
        for (int i = 0; i < messages.Length; i++) {
            if (messages[i].Id == newest) idxNewest = i;
            if (messages[i].Id == middle) idxMiddle = i;
            if (messages[i].Id == older)  idxOlder  = i;
        }

        Assert.Multiple(() => {
            Assert.That(idxNewest, Is.Not.EqualTo(-1), "T8: newest message must appear in the list");
            Assert.That(idxMiddle, Is.Not.EqualTo(-1), "T8: middle message must appear in the list");
            Assert.That(idxOlder,  Is.Not.EqualTo(-1), "T8: older message must appear in the list");
            Assert.That(idxNewest, Is.LessThan(idxMiddle),
                "T8 (CRITICAL): newest must appear before middle — default sort is DESC. " +
                "A failure here means the service's default-sort injection is missing.");
            Assert.That(idxMiddle, Is.LessThan(idxOlder),
                "T8: middle must appear before older in DESC order");
        });
    }

    // -----------------------------------------------------------------------
    // T9 — GET /{id} returns 403 to non-related caller
    // -----------------------------------------------------------------------

    [Test]
    public async Task T9_Get_OtherUsersMessage_Returns404_NotLeakingExistence()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long userA = await InsertUserAsync($"t9-A-{suffix}", Json.WriteString(new[] { "write" }));
        long userB = await InsertUserAsync($"t9-B-{suffix}", Json.WriteString(new[] { "write" }));
        long userC = await InsertUserAsync($"t9-C-{suffix}", Json.WriteString(new[] { "read" }));

        // A→B message; C is unrelated.
        long msgId = await db.Insert<Message>()
                             .Columns(m => m.AuthorId, m => m.RecipientId, m => m.Subject, m => m.Body, m => m.CreatedAt)
                             .Values(userA, userB, $"T9-A->B-{suffix}", "body", DateTime.UtcNow)
                             .ReturnID()
                             .ExecuteAsync();

        string tokenC = fixture.MintToken(userId: userC);
        HttpClient clientC = ClientWithToken(tokenC);

        // C requests the real (but unrelated) message — must get 404, not 403.
        HttpResponseMessage responseExisting = await clientC.GetAsync($"/api/messages/{msgId}");
        Assert.That((int)responseExisting.StatusCode, Is.EqualTo(404),
            "T9 (CRITICAL): GET /{id} must return 404 (not 403) to a caller who is neither author nor " +
            "recipient. A failure here means existence leaks to unrelated callers via the status code.");

        // Request a guaranteed-nonexistent id — must also get 404.
        HttpResponseMessage responseNonExistent = await clientC.GetAsync($"/api/messages/{long.MaxValue}");
        Assert.That((int)responseNonExistent.StatusCode, Is.EqualTo(404),
            "T9: GET for a never-existed id must also return 404.");

        // Load-bearing: the two 404 responses must be body-equivalent so existence cannot be inferred
        // from response shape differences. The framework echoes the requested id in the 404 body, so
        // normalise both bodies by replacing the numeric id token with a placeholder before comparing —
        // the shape must be identical; only the id value differs, which is expected and not a leak.
        string bodyExisting    = await responseExisting.Content.ReadAsStringAsync();
        string bodyNonExistent = await responseNonExistent.Content.ReadAsStringAsync();
        // Replace all digit-only sequences (the echoed id) with a fixed placeholder so the shape
        // comparison is not invalidated by the numeric id values themselves.
        System.Text.RegularExpressions.Regex digitRun = new System.Text.RegularExpressions.Regex(@"\d+");
        string normExisting    = digitRun.Replace(bodyExisting, "<id>");
        string normNonExistent = digitRun.Replace(bodyNonExistent, "<id>");
        Assert.That(normExisting, Is.EqualTo(normNonExistent),
            "T9 (CRITICAL): The 404 for an unrelated message must be body-equivalent (after id normalisation) " +
            "to the 404 for a never-existed id. A structural difference leaks existence information to the caller.");
    }

    // -----------------------------------------------------------------------
    // T10 — sender (author) cannot DELETE their sent message
    // -----------------------------------------------------------------------

    [Test]
    public async Task T10_Delete_BySender_Returns404()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long sender    = await InsertUserAsync($"t10-sender-{suffix}", Json.WriteString(new[] { "write" }));
        long recipient = await InsertUserAsync($"t10-recipient-{suffix}", Json.WriteString(new[] { "read" }));

        string senderToken = fixture.MintToken(userId: sender);
        HttpClient senderClient = ClientWithToken(senderToken);

        // Sender posts a message.
        HttpResponseMessage postResponse = await senderClient.PostAsync("/api/messages", JsonBody(new {
            recipientId = recipient,
            subject     = $"T10-recall-attempt-{suffix}",
            body        = "body"
        }));
        Assert.That((int)postResponse.StatusCode, Is.EqualTo(200));
        MessageDetails created = await ReadMessageAsync(postResponse);

        // Sender tries to DELETE — must get 404 (sender-cannot-recall rule).
        HttpResponseMessage deleteResponse = await senderClient.DeleteAsync($"/api/messages/{created.Id}");
        Assert.That((int)deleteResponse.StatusCode, Is.EqualTo(404),
            "T10 (CRITICAL): sender must receive 404 when attempting to DELETE their own sent message. " +
            "A failure here means the sender-cannot-recall rule is broken — senders can retract messages.");
    }

    // -----------------------------------------------------------------------
    // T11 — DELETE by recipient succeeds; second DELETE returns 404
    // -----------------------------------------------------------------------

    [Test]
    public async Task T11_Delete_ByRecipient_Succeeds_And_SecondDeleteReturns404()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long sender    = await InsertUserAsync($"t11-sender-{suffix}", Json.WriteString(new[] { "write" }));
        long recipient = await InsertUserAsync($"t11-recipient-{suffix}", Json.WriteString(new[] { "write" }));

        // Seed a message directly.
        long msgId = await db.Insert<Message>()
                             .Columns(m => m.AuthorId, m => m.RecipientId, m => m.Subject, m => m.Body, m => m.CreatedAt)
                             .Values(sender, recipient, $"T11-{suffix}", "body", DateTime.UtcNow)
                             .ReturnID()
                             .ExecuteAsync();

        string recipientToken = fixture.MintToken(userId: recipient);
        HttpClient recipientClient = ClientWithToken(recipientToken);

        // First DELETE — must succeed (200).
        HttpResponseMessage first = await recipientClient.DeleteAsync($"/api/messages/{msgId}");
        Assert.That((int)first.StatusCode, Is.EqualTo(200),
            "T11: first DELETE by recipient must return 200");

        // Second DELETE — must return 404 (optimistic pattern: gone is gone).
        HttpResponseMessage second = await recipientClient.DeleteAsync($"/api/messages/{msgId}");
        Assert.That((int)second.StatusCode, Is.EqualTo(404),
            "T11: second DELETE must return 404, not 500. " +
            "A failure here means the optimistic delete throws an unhandled exception on double-delete.");
    }

    // -----------------------------------------------------------------------
    // T12 — admin can DELETE any message
    // -----------------------------------------------------------------------

    [Test]
    public async Task T12_Delete_ByAdmin_Succeeds_OnAnyMessage()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long adminId   = await InsertUserAsync($"t12-admin-{suffix}", Json.WriteString(new[] { "admin" }));
        long userA     = await InsertUserAsync($"t12-A-{suffix}", Json.WriteString(new[] { "write" }));
        long userB     = await InsertUserAsync($"t12-B-{suffix}", Json.WriteString(new[] { "read" }));

        // A→B message; admin is uninvolved.
        long msgId = await db.Insert<Message>()
                             .Columns(m => m.AuthorId, m => m.RecipientId, m => m.Subject, m => m.Body, m => m.CreatedAt)
                             .Values(userA, userB, $"T12-{suffix}", "body", DateTime.UtcNow)
                             .ReturnID()
                             .ExecuteAsync();

        string adminToken = fixture.MintToken(userId: adminId);
        HttpClient adminClient = ClientWithToken(adminToken);

        HttpResponseMessage response = await adminClient.DeleteAsync($"/api/messages/{msgId}");
        Assert.That((int)response.StatusCode, Is.EqualTo(200),
            "T12: admin must be able to DELETE any message regardless of author/recipient. " +
            "A failure here means the admin bypass in Delete is broken.");

        // Confirm the row is gone.
        Message? dbRow = await db.Load<Message>().Where(m => m.Id == msgId).ExecuteEntityAsync();
        Assert.That(dbRow, Is.Null,
            "T12: DB row must be gone after admin delete");
    }

    // -----------------------------------------------------------------------
    // T13 — PATCH returns 404 or 405 (patch surface is absent)
    // -----------------------------------------------------------------------

    [Test]
    public async Task T13_Patch_AnyMessageProperty_Returns404OrMethodNotAllowed()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long userA = await InsertUserAsync($"t13-A-{suffix}", Json.WriteString(new[] { "write" }));
        long userB = await InsertUserAsync($"t13-B-{suffix}", Json.WriteString(new[] { "read" }));

        long msgId = await db.Insert<Message>()
                             .Columns(m => m.AuthorId, m => m.RecipientId, m => m.Subject, m => m.Body, m => m.CreatedAt)
                             .Values(userA, userB, $"T13-{suffix}", "body", DateTime.UtcNow)
                             .ReturnID()
                             .ExecuteAsync();

        string tokenA = fixture.MintToken(userId: userA);
        HttpClient clientA = ClientWithToken(tokenA);

        HttpRequestMessage request = new(HttpMethod.Patch, $"/api/messages/{msgId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.MintToken(userId: userA));
        request.Content = JsonBody(new object[] { new { op = "replace", path = "/subject", value = "hacked" } });

        HttpResponseMessage response = await clientA.SendAsync(request);

        int status = (int)response.StatusCode;
        Assert.That(status, Is.EqualTo(404).Or.EqualTo(405),
            "T13 (CRITICAL): PATCH on /api/messages/{id} must return 404 or 405 — the patch surface " +
            "must be structurally absent. A 200 or 400 here means someone added [HttpPatch] or " +
            "[AllowPatch] to the message controller or entity.");
    }

    // -----------------------------------------------------------------------
    // T14 — subject is trimmed of leading/trailing whitespace server-side
    // -----------------------------------------------------------------------

    [Test]
    public async Task T14_Subject_Trimmed_LeadingTrailingWhitespace_ServerSide()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long authorId    = await InsertUserAsync($"t14-author-{suffix}", Json.WriteString(new[] { "write" }));
        long recipientId = await InsertUserAsync($"t14-recipient-{suffix}", Json.WriteString(new[] { "read" }));

        string token = fixture.MintToken(userId: authorId);
        HttpClient client = ClientWithToken(token);

        HttpResponseMessage response = await client.PostAsync("/api/messages", JsonBody(new {
            recipientId = recipientId,
            subject     = "   trimmed subject   ",
            body        = "body"
        }));

        Assert.That((int)response.StatusCode, Is.EqualTo(200),
            "T14: POST with padded subject must succeed");

        MessageDetails created = await ReadMessageAsync(response);
        Assert.That(created.Subject, Is.EqualTo("trimmed subject"),
            "T14: leading and trailing whitespace must be stripped from subject server-side. " +
            "A failure here means the service's subject-trim call is missing.");
    }
}
