using System.Diagnostics;
using System.Net;
using Backend.Models.Messages;
using Backend.tests.Fixtures;
using Npgsql;
using Pooshit.Json;
using Pooshit.Ocelot.Entities;

namespace Backend.tests.Tests;

/// <summary>Exercises reply validation and competing claims at the external-send boundary.</summary>
[TestFixture, Parallelizable]
public class TeamsReplyBoundaryTests {
    /// <summary>Checks UTF-8 limits before any attempt is claimed or sent.</summary>
    /// <param name="scenario">The boundary payload scenario.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase("blank"), TestCase("empty"), TestCase("oversized"), TestCase("boundary"), Parallelizable]
    public async Task ReplyAsync_BodyBoundary_ValidatesBeforeClaimAndSend(string scenario) {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        using TeamsRoutingFixture fixture = new(database);
        using HttpResponseMessage received = await fixture.Host.Post(fixture.Host.Token());
        Assert.That(received.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Message message = await database.Entities.Load<Message>().ExecuteEntityAsync();
        Assert.That(message.Id, Is.GreaterThan(0));
        string body = scenario switch {
            "blank" => " \t\r\n\u2003", "empty" => "", "oversized" => new string('é', 8193), _ => new string('é', 8192)
        };

        using HttpResponseMessage response = await fixture.Reply(message.Id, body);

        bool accepted = scenario == "boundary";
        Assert.That(response.StatusCode, Is.EqualTo(accepted ? HttpStatusCode.OK : HttpStatusCode.BadRequest));
        Assert.That(fixture.Host.Connector.Bodies, Has.Count.EqualTo(accepted ? 1 : 0));
        TeamsMessageOrigin origin = await database.Entities.Load<TeamsMessageOrigin>()
            .Where(route => route.MessageId == message.Id).ExecuteEntityAsync();
        Assert.That(origin.ReplyState, Is.EqualTo(accepted ? TeamsReplyState.Sent : TeamsReplyState.Pending));
        if (accepted) {
            Dictionary<string, object> sent = Json.Read<Dictionary<string, object>>(fixture.Host.Connector.Bodies.Single());
            Assert.That(sent["text"], Is.EqualTo(body));
        } else {
            Assert.That(origin.ReplyBodyHash, Is.Null);
            Assert.That(origin.ReplyAttemptStartedAt, Is.Null);
            using HttpResponseMessage valid = await fixture.Reply(message.Id, "explicit valid response after rejection");
            Assert.That(valid.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(fixture.Host.Connector.Bodies, Has.Count.EqualTo(1));
        }
    }

    /// <summary>Forces two Pending reads to compete for one database claim.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Test, Parallelizable]
    public async Task ReplyAsync_TwoPendingReads_OnlySuccessfulDatabaseClaimSends() {
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(true);
        using TeamsRoutingFixture fixture = new(database);
        using HttpResponseMessage received = await fixture.Host.Post(fixture.Host.Token());
        Assert.That(received.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Message message = await database.Entities.Load<Message>().ExecuteEntityAsync();
        Assert.That(message.Id, Is.GreaterThan(0));
        await using NpgsqlConnection locked = database.CreatePostgresConnection();
        await locked.OpenAsync();
        await using NpgsqlCommand tableQuery = new("SELECT quote_ident(table_name) FROM information_schema.tables WHERE table_schema=current_schema() AND lower(table_name)='teamsmessageorigin'", locked);
        string table = (string)(await tableQuery.ExecuteScalarAsync())!;
        await using NpgsqlCommand columnQuery = new("SELECT quote_ident(column_name) FROM information_schema.columns WHERE table_schema=current_schema() AND lower(table_name)='teamsmessageorigin' AND lower(column_name)='messageid'", locked);
        string column = (string)(await columnQuery.ExecuteScalarAsync())!;
        await using NpgsqlTransaction rowLock = await locked.BeginTransactionAsync();
        await using NpgsqlCommand acquire = new($"SELECT {column} FROM {table} WHERE {column}=@id FOR UPDATE", locked, rowLock);
        acquire.Parameters.AddWithValue("id", message.Id);
        Assert.That(await acquire.ExecuteScalarAsync(), Is.EqualTo(message.Id));
        Task<HttpResponseMessage> first = fixture.Reply(message.Id);
        Task<HttpResponseMessage> second = fixture.Reply(message.Id);
        await using NpgsqlConnection observer = database.CreatePostgresConnection();
        await observer.OpenAsync();
        int waiting = 0;
        try {
            Stopwatch deadline = Stopwatch.StartNew();
            while (waiting < 2 && deadline.Elapsed < TimeSpan.FromSeconds(5)) {
                await using NpgsqlCommand count = new("SELECT count(*)::int FROM pg_stat_activity WHERE application_name=current_setting('application_name') AND wait_event_type='Lock' AND lower(query) LIKE '%teamsmessageorigin%'", observer);
                waiting = (int)(await count.ExecuteScalarAsync())!;
                if (waiting < 2)
                    await Task.Delay(10);
            }
        } finally {
            await rowLock.RollbackAsync();
        }
        HttpResponseMessage[] responses = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(20));
        HttpStatusCode[] codes = responses.Select(response => response.StatusCode).ToArray();
        foreach (HttpResponseMessage response in responses)
            response.Dispose();

        Assert.That(waiting, Is.EqualTo(2), "Both requests must reach the locked database claim before release.");
        Assert.That(codes.Count(code => code == HttpStatusCode.OK), Is.EqualTo(1));
        Assert.That(codes.Count(code => code == HttpStatusCode.Conflict), Is.EqualTo(1));
        Assert.That(fixture.Host.Connector.Bodies, Has.Count.EqualTo(1));
        TeamsMessageOrigin origin = await database.Entities.Load<TeamsMessageOrigin>()
            .Where(route => route.MessageId == message.Id).ExecuteEntityAsync();
        Assert.That(origin.ReplyState, Is.EqualTo(TeamsReplyState.Sent));
    }
}
