using System.Net;
using Backend.Models.Messages;
using Backend.tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Tokens;

namespace Backend.tests.Tests;

/// <summary>Exercises sanitized Teams webhook events through real authentication and SDK boundaries.</summary>
[TestFixture, Parallelizable]
public class TeamsDiagnosticsTests {
    /// <summary>Checks that unrelated HTTP routes do not enter Teams diagnostics.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Test, Parallelizable]
    public async Task ProcessAsync_HealthRoute_DoesNotEmitTeamsEvents() {
        TeamsLogCapture capture = new();
        using TeamsWebhookFixture fixture = new(configureServices: services => Capture(services, capture));

        using HttpResponseMessage response = await fixture.Send(HttpMethod.Get, "api/health");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(capture.Messages, Has.None.Contains("event=teams."));
    }

    /// <summary>Checks that authentication rejection remains observable before handler execution.</summary>
    /// <param name="authEnabled">Whether ordinary DiVoid authentication is enabled.</param>
    /// <param name="path">The canonical or trailing-slash webhook route.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(true, "api/teams/messages"), TestCase(false, "api/teams/messages"),
     TestCase(true, "api/teams/messages/"), TestCase(false, "api/teams/messages/"), Parallelizable]
    public async Task ProcessAsync_UnsignedRequest_RecordsMatchedPolicyAnd401(bool authEnabled, string path) {
        TeamsLogCapture capture = new();
        using TeamsWebhookFixture fixture = new(authEnabled, configureServices: services => Capture(services, capture));

        using HttpResponseMessage response = await fixture.PostBody(null, new StringContent("{}"), path);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(capture.Messages, Has.Some.Contains("event=teams.webhook.arrived").And.Some.Contains("endpoint=teams teamsPolicy=True"));
        Assert.That(capture.Messages, Has.Some.Contains("status=401 authenticated=False outcome=completed"));
        Assert.That(fixture.Connector.Bodies, Is.Empty);
        Assert.That(capture.Messages, Has.None.Contains("event=teams.setup.send_started"));
    }

    /// <summary>Checks ordinary disabled-route fallback without inventing an anonymous route.</summary>
    /// <param name="authEnabled">Whether ordinary DiVoid authentication is enabled.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(true), TestCase(false), Parallelizable]
    public async Task ProcessAsync_DisabledRoute_RecordsUnmatchedFallback(bool authEnabled) {
        TeamsLogCapture capture = new();
        using TeamsWebhookFixture fixture = new(authEnabled, new() { ["Teams:Enabled"] = "false" },
            services => Capture(services, capture));

        using HttpResponseMessage response = await fixture.Post(null);

        int expected = authEnabled ? 401 : 404;
        Assert.That((int)response.StatusCode, Is.EqualTo(expected));
        Assert.That(capture.Messages, Has.Some.Contains("endpoint=none teamsPolicy=False"));
        Assert.That(capture.Messages, Has.Some.Contains($"status={expected} authenticated=False outcome=completed"));
        Assert.That(capture.Messages, Has.None.Contains("event=teams.webhook.decision"));
        Assert.That(fixture.Connector.Bodies, Is.Empty);
    }

    /// <summary>Checks actual setup-send acceptance and diagnostic privacy with trace logging enabled.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Test, Parallelizable]
    public async Task ProcessAsync_SetupSuccess_RecordsAcceptanceWithoutSensitiveData() {
        TeamsLogCapture capture = new();
        using TeamsWebhookFixture fixture = new(configureServices: services => Capture(services, capture));
        string token = fixture.Token();
        Dictionary<string, object?> activity = TeamsWebhookFixture.Activity();
        activity["text"] = "private-diagnostics-body-marker";

        using HttpResponseMessage response = await fixture.PostBody(token,
            new StringContent(Pooshit.Json.Json.WriteString(activity), System.Text.Encoding.UTF8, "application/json"),
            "api/teams/messages?private-diagnostics-query-marker");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(fixture.Connector.Bodies, Has.Count.EqualTo(1));
        Assert.That(capture.Messages, Has.Some.Contains("event=teams.setup.send_started").And.Some.Contains("event=teams.setup.send_accepted"));
        Assert.That(capture.Messages, Has.Some.Contains("outcome=setup_completed status=200"));
        Assert.That(capture.Messages, Has.Some.Contains("status=200 authenticated=True outcome=completed"));
        string[] sensitive = [token, "private-diagnostics-body-marker", "private-diagnostics-query-marker",
            "synthetic-fixture-secret", TeamsWebhookFixture.ClientId, TeamsWebhookFixture.TenantId,
            TeamsWebhookFixture.ServiceUrl, "conversation-1", "activity-1", "sender-1"];
        Assert.That(capture.Messages.Where(message => message.StartsWith("event=teams."))
            .Any(message => sensitive.Any(message.Contains)), Is.False);
    }

    /// <summary>Checks actual SDK failure and cancellation without logging native exception messages.</summary>
    /// <param name="cancelled">Whether the Connector reports cancellation.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task ProcessAsync_SetupSendFailure_RecordsFailureAndFinalStatus(bool cancelled) {
        TeamsLogCapture capture = new();
        using TeamsWebhookFixture fixture = new(configureServices: services => Capture(services, capture));
        fixture.Connector.Status = HttpStatusCode.BadGateway;
        fixture.Connector.CancelSend = cancelled;

        using HttpResponseMessage response = await fixture.Post(fixture.Token());

        int expected = cancelled ? 504 : 502;
        Assert.That((int)response.StatusCode, Is.EqualTo(expected));
        Assert.That(fixture.Connector.Bodies, Has.Count.EqualTo(1));
        Assert.That(capture.Messages, Has.Some.Contains("event=teams.setup.send_started").And.Some.Contains("event=teams.setup.send_failed"));
        Assert.That(capture.Messages, Has.Some.Contains($"outcome=setup_failed status={expected}"));
        Assert.That(capture.Messages, Has.Some.Contains($"status={expected} authenticated=True outcome=completed"));
        Assert.That(capture.Messages, Has.None.Contains("event=teams.setup.send_accepted"));
        Assert.That(capture.Messages.Any(message => message.Contains(TeamsWebhookFixture.ServiceUrl) ||
            message.Contains("conversation-1") || message.Contains("sensitive text must never be echoed")), Is.False);
    }

    /// <summary>Checks authenticated ignored and rejected activities without outbound sends.</summary>
    /// <param name="ignored">Whether the message has no text rather than an invalid channel.</param>
    /// <returns>The asynchronous test operation.</returns>
    [TestCase(true), TestCase(false), Parallelizable]
    public async Task ProcessAsync_NoSendBranch_RecordsAuthenticatedDecision(bool ignored) {
        TeamsLogCapture capture = new();
        using TeamsWebhookFixture fixture = new(configureServices: services => Capture(services, capture));
        Dictionary<string, object?> activity = TeamsWebhookFixture.Activity();
        activity[ignored ? "text" : "channelId"] = ignored ? "" : "not-msteams";

        using HttpResponseMessage response = await fixture.Post(fixture.Token(), activity);

        int expected = ignored ? 200 : 403;
        Assert.That((int)response.StatusCode, Is.EqualTo(expected));
        Assert.That(capture.Messages, Has.Some.Contains(ignored ? "outcome=activity_ignored" : "outcome=tenant_or_channel_rejected"));
        Assert.That(capture.Messages, Has.Some.Contains($"status={expected} authenticated=True outcome=completed"));
        Assert.That(fixture.Connector.Bodies, Is.Empty);
    }

    /// <summary>Checks queued inbox routing is distinguished from setup reply acceptance.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Test, Parallelizable]
    public async Task ProcessAsync_BoundMessage_RecordsQueuedWithoutSetupSend() {
        TeamsLogCapture capture = new();
        using TeamsRoutingDatabase database = await TeamsRoutingDatabase.CreateAsync(false);
        using TeamsRoutingFixture fixture = new(database, configureServices: services => Capture(services, capture));

        using HttpResponseMessage response = await fixture.Host.Post(fixture.Host.Token());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await database.Entities.Load<Message>(DB.Count()).ExecuteScalarAsync<long>(), Is.EqualTo(1));
        Assert.That(capture.Messages, Has.Some.Contains("outcome=bound_queued status=200"));
        Assert.That(capture.Messages, Has.None.Contains("event=teams.setup.send_started"));
        Assert.That(fixture.Host.Connector.Bodies, Is.Empty);
    }

    static void Capture(IServiceCollection services, TeamsLogCapture capture) {
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(capture));
        services.PostConfigure<LoggerFilterOptions>(options => {
            foreach (LoggerFilterRule rule in options.Rules.Where(rule => rule.CategoryName != "Microsoft.Teams").ToArray())
                options.Rules.Remove(rule);
            options.MinLevel = LogLevel.Trace;
        });
    }
}
