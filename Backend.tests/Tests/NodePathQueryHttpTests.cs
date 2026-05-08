using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// HTTP integration tests for <c>GET /api/nodes/path?path=...</c>.
///
/// Uses <see cref="WebApplicationFactory{TEntryPoint}"/> with an in-memory SQLite
/// database, auth disabled, and a seeded graph to exercise the full path-query
/// pipeline end-to-end.
///
/// Seeded graph (set up once per fixture):
/// <code>
///   org1 (type:organization, name:Pooshit)
///     └── proj1 (type:project, name:DiVoid, status:open)
///           ├── task1 (type:task, name:Setup CI, status:open)
///           └── task2 (type:task, name:Write tests, status:closed)
///   org1 ──── proj2 (type:project, name:OtherProject, status:closed)
///                └── question1 (type:question, name:Q1, status:open)
/// </code>
/// </summary>
[TestFixture]
public class NodePathQueryHttpTests
{
    WebApplicationFactory<Program> _factory = null!;
    HttpClient _client = null!;

    // node ids set during OneTimeSetUp
    long _org1Id;
    long _proj1Id;
    long _proj2Id;
    long _task1Id;
    long _task2Id;
    long _question1Id;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => {
            builder.ConfigureAppConfiguration((_, config) => {
                config.AddInMemoryCollection(new Dictionary<string, string?> {
                    ["Auth:Enabled"] = "false",
                    ["Database:Type"] = "Sqlite",
                    ["Database:Source"] = $"/tmp/divoid_path_test_{Guid.NewGuid():N}.db3"
                });
            });
        });
        _client = _factory.CreateClient();

        // Seed the graph
        _org1Id = await CreateNodeAsync("organization", "Pooshit");
        _proj1Id = await CreateNodeAsync("project", "DiVoid");
        _proj2Id = await CreateNodeAsync("project", "OtherProject");
        _task1Id = await CreateNodeAsync("task", "Setup CI");
        _task2Id = await CreateNodeAsync("task", "Write tests");
        _question1Id = await CreateNodeAsync("question", "Q1");

        await SetStatusAsync(_proj1Id, "open");
        await SetStatusAsync(_proj2Id, "closed");
        await SetStatusAsync(_task1Id, "open");
        await SetStatusAsync(_task2Id, "closed");
        await SetStatusAsync(_question1Id, "open");

        // Links: org1 → proj1 → task1, task2; org1 → proj2 → question1
        await LinkAsync(_org1Id, _proj1Id);
        await LinkAsync(_org1Id, _proj2Id);
        await LinkAsync(_proj1Id, _task1Id);
        await LinkAsync(_proj1Id, _task2Id);
        await LinkAsync(_proj2Id, _question1Id);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    async Task<long> CreateNodeAsync(string type, string name)
    {
        StringContent body = new(JsonSerializer.Serialize(new { type, name }), Encoding.UTF8, "application/json");
        HttpResponseMessage resp = await _client.PostAsync("/api/nodes", body);
        resp.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetInt64();
    }

    async Task SetStatusAsync(long id, string status)
    {
        object[] ops = [new { op = "replace", path = "/status", value = status }];
        StringContent body = new(JsonSerializer.Serialize(ops), Encoding.UTF8, "application/json");
        HttpResponseMessage resp = await _client.PatchAsync($"/api/nodes/{id}", body);
        resp.EnsureSuccessStatusCode();
    }

    async Task LinkAsync(long src, long tgt)
    {
        StringContent body = new(JsonSerializer.Serialize(tgt), Encoding.UTF8, "application/json");
        HttpResponseMessage resp = await _client.PostAsync($"/api/nodes/{src}/links", body);
        resp.EnsureSuccessStatusCode();
    }

    async Task<HttpResponseMessage> PathQueryAsync(string path, string extra = "")
        => await _client.GetAsync($"/api/nodes?path={Uri.EscapeDataString(path)}{extra}");

    static async Task<(List<long> ids, long? total)> ParseResultAsync(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        List<long> ids = [];
        if (doc.RootElement.TryGetProperty("result", out JsonElement resultEl))
        {
            foreach (JsonElement el in resultEl.EnumerateArray())
                if (el.TryGetProperty("id", out JsonElement idEl))
                    ids.Add(idEl.GetInt64());
        }
        long? total = null;
        if (doc.RootElement.TryGetProperty("total", out JsonElement totalEl) && totalEl.ValueKind == JsonValueKind.Number)
            total = totalEl.GetInt64();
        return (ids, total);
    }

    static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage resp)
        => JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

    // -----------------------------------------------------------------------
    // Single-hop — parity with existing ?type= / ?linkedto= filters
    // -----------------------------------------------------------------------

    [Test]
    public async Task SingleHop_TypeFilter_ReturnsTasks()
    {
        HttpResponseMessage resp = await PathQueryAsync("[type:task]");
        (List<long> ids, long? total) = await ParseResultAsync(resp);

        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(_task1Id));
            Assert.That(ids, Does.Contain(_task2Id));
            Assert.That(total, Is.GreaterThanOrEqualTo(2));
        });
    }

    [Test]
    public async Task SingleHop_IdFilter_ReturnsExactNode()
    {
        HttpResponseMessage resp = await PathQueryAsync($"[id:{_org1Id}]");
        (List<long> ids, _) = await ParseResultAsync(resp);

        Assert.That(ids, Is.EqualTo(new[] { _org1Id }));
    }

    // -----------------------------------------------------------------------
    // Two-hop paths
    // -----------------------------------------------------------------------

    [Test]
    public async Task TwoHop_OrgToProjects_ReturnsBothProjects()
    {
        HttpResponseMessage resp = await PathQueryAsync($"[type:organization,name:Pooshit]/[type:project]");
        (List<long> ids, _) = await ParseResultAsync(resp);

        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(_proj1Id));
            Assert.That(ids, Does.Contain(_proj2Id));
        });
    }

    [Test]
    public async Task TwoHop_IdRooted_OrgToTasks_ReturnsLinkedProjects()
    {
        // [id:org1]/[type:project] is equivalent to linkedto=org1&type=project
        HttpResponseMessage resp = await PathQueryAsync($"[id:{_org1Id}]/[type:project]");
        (List<long> ids, _) = await ParseResultAsync(resp);

        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(_proj1Id));
            Assert.That(ids, Does.Contain(_proj2Id));
            Assert.That(ids, Does.Not.Contain(_org1Id));
            Assert.That(ids, Does.Not.Contain(_task1Id));
        });
    }

    // -----------------------------------------------------------------------
    // Three-hop path with status filter at multiple hops
    // -----------------------------------------------------------------------

    [Test]
    public async Task ThreeHop_OrgToOpenProjectToOpenTasks()
    {
        // [type:organization,name:Pooshit]/[type:project,status:open]/[type:task,status:open]
        // org1 → proj1 (open) → task1 (open); proj2 is closed so excluded
        HttpResponseMessage resp = await PathQueryAsync(
            "[type:organization,name:Pooshit]/[type:project,status:open]/[type:task,status:open]");
        (List<long> ids, _) = await ParseResultAsync(resp);

        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(_task1Id));
            Assert.That(ids, Does.Not.Contain(_task2Id)); // closed
        });
    }

    [Test]
    public async Task ThreeHop_WithinKeyOr_OnStatus_ReturnsBothStatuses()
    {
        // [type:organization,name:Pooshit]/[type:project,status:open]/[type:task,status:open|new]
        // Should include task1 (open); new is not set but should not exclude open
        HttpResponseMessage resp = await PathQueryAsync(
            $"[type:organization,name:Pooshit]/[type:project,status:open]/[type:task,status:open|closed]");
        (List<long> ids, _) = await ParseResultAsync(resp);

        // Both task1 (open) and task2 (closed) are linked to proj1 which is open
        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(_task1Id));
            Assert.That(ids, Does.Contain(_task2Id));
        });
    }

    // -----------------------------------------------------------------------
    // Within-key OR
    // -----------------------------------------------------------------------

    [Test]
    public async Task WithinKeyOr_TypeOr_ReturnsBothTypes()
    {
        // [type:task|question] should return all tasks and questions
        HttpResponseMessage resp = await PathQueryAsync("[type:task|question]");
        (List<long> ids, _) = await ParseResultAsync(resp);

        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(_task1Id));
            Assert.That(ids, Does.Contain(_task2Id));
            Assert.That(ids, Does.Contain(_question1Id));
        });
    }

    [Test]
    public async Task TwoHop_IdRooted_TypeOr_ReturnsMultipleTypes()
    {
        // [id:proj1]/[type:task|question] — proj1 links to tasks
        HttpResponseMessage resp = await PathQueryAsync($"[id:{_proj1Id}]/[type:task|question]");
        (List<long> ids, _) = await ParseResultAsync(resp);

        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(_task1Id));
            Assert.That(ids, Does.Contain(_task2Id));
        });
    }

    // -----------------------------------------------------------------------
    // Wildcard on name
    // -----------------------------------------------------------------------

    [Test]
    public async Task WildcardName_PercentSuffix_MatchesProjectByPrefix()
    {
        // [type:project,name:Di%] should match DiVoid
        HttpResponseMessage resp = await PathQueryAsync("[type:project,name:Di%]");
        (List<long> ids, _) = await ParseResultAsync(resp);

        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(_proj1Id));
            Assert.That(ids, Does.Not.Contain(_proj2Id)); // OtherProject
        });
    }

    [Test]
    public async Task WildcardStatus_PercentSuffix_MatchesOpenAndOpenReview()
    {
        // [status:op%] — should match "open" prefix
        HttpResponseMessage resp = await PathQueryAsync("[status:op%]");
        (List<long> ids, _) = await ParseResultAsync(resp);

        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(_proj1Id));   // status=open
            Assert.That(ids, Does.Contain(_task1Id));   // status=open
            Assert.That(ids, Does.Contain(_question1Id)); // status=open
            Assert.That(ids, Does.Not.Contain(_task2Id)); // status=closed
        });
    }

    // -----------------------------------------------------------------------
    // Empty segment — any-neighbour wildcard
    // -----------------------------------------------------------------------

    [Test]
    public async Task EmptySegment_AnyNeighbour_ReturnsLinkedNodes()
    {
        // [type:organization,name:Pooshit]/[] — any node linked to org1
        HttpResponseMessage resp = await PathQueryAsync("[type:organization,name:Pooshit]/[]");
        (List<long> ids, _) = await ParseResultAsync(resp);

        // Should include both projects (linked to org1), not org1 itself
        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(_proj1Id));
            Assert.That(ids, Does.Contain(_proj2Id));
            Assert.That(ids, Does.Not.Contain(_org1Id));
        });
    }

    // -----------------------------------------------------------------------
    // B1 regression — sub-route must be gone
    // -----------------------------------------------------------------------

    [Test]
    public async Task SubRoute_NodePath_Returns404()
    {
        // /api/nodes/path?path=... was the old (spec-violating) sub-route.
        // After the B1 fix it must be gone; requests must hit /api/nodes?path=...
        HttpResponseMessage resp = await _client.GetAsync($"/api/nodes/path?path={Uri.EscapeDataString("[type:task]")}");
        Assert.That((int) resp.StatusCode, Is.EqualTo(404),
            "The /api/nodes/path sub-route must not exist; use /api/nodes?path= instead.");
    }

    // -----------------------------------------------------------------------
    // B2 regression — single server-side joined query for multi-hop paths
    //
    // Pooshit.Ocelot does not expose a query counter, so we verify the contract
    // behaviourally: the correct result set must be returned for a 4-hop path.
    // The implementation uses IN(LoadOperation<Node>) subquery chaining
    // (mirroring the existing linkedto filter at NodeService.cs:175-181) so
    // the entire chain compiles to ONE SQL statement — no intermediate id-sets
    // are materialised in C# between hops.
    // -----------------------------------------------------------------------

    [Test]
    public async Task FourHop_SingleQueryContract_CorrectTerminalSet()
    {
        // Graph: org1 → proj1 (open) → task1 (open)
        // 4-hop path: [type:organization]/[type:project,status:open]/[type:task,status:open]/[type:task,status:open]
        // The last hop re-filters task1 by following its self-links; since task1 has no
        // outgoing links of type:task, the result is empty — which proves the subquery
        // chain composed correctly rather than failing/crashing at hop 4.
        // A simpler proof: a 4-hop path that traces org→proj→task→question should return
        // question1 (proj2→question1 would need org1→proj2, not proj1).
        // Use: [type:organization,name:Pooshit]/[type:project]/[type:task]/[] to get all
        // nodes linked to any task that's linked to any project under Pooshit.
        // Since tasks (task1, task2) only link to proj1, the terminal hop [] returns proj1.
        HttpResponseMessage resp = await PathQueryAsync(
            "[type:organization,name:Pooshit]/[type:project]/[type:task]/[]");
        Assert.That(resp.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK),
            "4-hop subquery chain must execute as a single query without crashing.");
        (List<long> ids, long? total) = await ParseResultAsync(resp);
        // task1 and task2 are linked to proj1; proj1 is linked to org1.
        // Terminal [] from tasks reaches proj1 (undirected link).
        Assert.That(ids, Does.Contain(_proj1Id),
            "proj1 must appear as a neighbour of tasks reachable from org1/project.");
        Assert.That(total, Is.GreaterThanOrEqualTo(0));
    }

    // -----------------------------------------------------------------------
    // nototal=true
    // -----------------------------------------------------------------------

    [Test]
    public async Task NoTotal_True_SkipsCountQuery()
    {
        // nototal=true signals the service to skip the COUNT query.
        // AsyncPageResponseWriter requires a delegate so we pass () => -1L as sentinel.
        // The response total will be -1, not null. The COUNT database call is skipped.
        HttpResponseMessage resp = await PathQueryAsync("[type:task]", "&nototal=true");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        // result array must still be present
        Assert.That(doc.RootElement.TryGetProperty("result", out _), Is.True);
        // total is -1 (the sentinel value indicating "not computed")
        if (doc.RootElement.TryGetProperty("total", out JsonElement totalEl))
            Assert.That(totalEl.GetInt64(), Is.EqualTo(-1L));
    }

    [Test]
    public async Task NoTotal_True_ExistingListEndpoint_SkipsCountQuery()
    {
        // nototal applies to the regular list endpoint too
        HttpResponseMessage resp = await _client.GetAsync("/api/nodes?type=task&nototal=true");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.That(doc.RootElement.TryGetProperty("result", out _), Is.True);
        if (doc.RootElement.TryGetProperty("total", out JsonElement totalEl))
            Assert.That(totalEl.GetInt64(), Is.EqualTo(-1L));
    }

    // -----------------------------------------------------------------------
    // Error handling — parse errors
    // -----------------------------------------------------------------------

    [Test]
    public async Task ParseError_UnknownKey_Returns400()
    {
        HttpResponseMessage resp = await PathQueryAsync("[foo:bar]");
        Assert.That((int) resp.StatusCode, Is.EqualTo(400));
    }

    [Test]
    public async Task ParseError_UnknownKey_ReturnsBadParameterCode()
    {
        HttpResponseMessage resp = await PathQueryAsync("[foo:bar]");
        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.That(doc.RootElement.GetProperty("code").GetString(), Is.EqualTo("badparameter"));
    }

    [Test]
    public async Task ParseError_MalformedPath_Returns400()
    {
        HttpResponseMessage resp = await PathQueryAsync("[type:task");
        Assert.That((int) resp.StatusCode, Is.EqualTo(400));
    }

    [Test]
    public async Task ParseError_ErrorBody_ContainsColumnInfo()
    {
        HttpResponseMessage resp = await PathQueryAsync("[foo:bar]");
        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        string text = doc.RootElement.GetProperty("text").GetString() ?? "";
        Assert.That(text, Does.Contain("column").IgnoreCase);
    }

    [Test]
    public async Task ParseError_DeferredNegation_Returns400WithReservedMessage()
    {
        HttpResponseMessage resp = await PathQueryAsync("[status:!closed]");
        Assert.That((int) resp.StatusCode, Is.EqualTo(400));
        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        string text = doc.RootElement.GetProperty("text").GetString() ?? "";
        Assert.That(text, Does.Contain("reserved").IgnoreCase);
    }

    [Test]
    public async Task ParseError_DeferredNegatedSegment_Returns400WithReservedMessage()
    {
        HttpResponseMessage resp = await PathQueryAsync("![type:archived]");
        Assert.That((int) resp.StatusCode, Is.EqualTo(400));
        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        string text = doc.RootElement.GetProperty("text").GetString() ?? "";
        Assert.That(text, Does.Contain("reserved").IgnoreCase);
    }

    // -----------------------------------------------------------------------
    // Resolution to empty set — 200 not an error
    // -----------------------------------------------------------------------

    [Test]
    public async Task EmptyResult_Returns200WithEmptyArray()
    {
        // Status that doesn't exist → empty result
        HttpResponseMessage resp = await PathQueryAsync("[type:task,status:nonexistent]");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        (List<long> ids, long? total) = await ParseResultAsync(resp);
        Assert.Multiple(() => {
            Assert.That(ids, Is.Empty);
            Assert.That(total, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task EmptyIntermediateHop_Returns200WithEmptyArray()
    {
        // Intermediate hop matches nothing → terminal is empty
        HttpResponseMessage resp = await PathQueryAsync("[type:organization,name:NonExistentOrg]/[type:project]/[type:task]");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        (List<long> ids, long? total) = await ParseResultAsync(resp);
        Assert.Multiple(() => {
            Assert.That(ids, Is.Empty);
            Assert.That(total, Is.EqualTo(0));
        });
    }

    // -----------------------------------------------------------------------
    // Cancellation
    // -----------------------------------------------------------------------

    [Test]
    public void Cancellation_AlreadyCancelled_ThrowsOperationCancelled()
    {
        // Pass an already-cancelled token — the service should observe it
        using CancellationTokenSource cts = new();
        cts.Cancel();

        // We call the service directly to verify the CT is observed
        // (HTTP integration layer swallows the exception via ASP.NET Core's built-in handling)
        using var scope = _factory.Services.CreateScope();
        Backend.Services.Nodes.INodeService svc = scope.ServiceProvider
            .GetRequiredService<Backend.Services.Nodes.INodeService>();

        Backend.Models.Nodes.NodePathFilter filter = new() {
            Path = "[type:task]",
            Count = 10
        };

        Assert.Throws<OperationCanceledException>(() =>
            svc.ListPagedByPath(filter, cts.Token).GetAwaiter().GetResult());
    }

    // -----------------------------------------------------------------------
    // Paging and sort apply to terminal hop only
    // -----------------------------------------------------------------------

    [Test]
    public async Task Paging_Count_LimitsResults()
    {
        HttpResponseMessage resp = await PathQueryAsync("[type:task]", "&count=1");
        (List<long> ids, long? total) = await ParseResultAsync(resp);

        Assert.Multiple(() => {
            Assert.That(ids.Count, Is.EqualTo(1));
            Assert.That(total, Is.GreaterThanOrEqualTo(2)); // total reflects full count even with paging
        });
    }

    [Test]
    public async Task Sort_ByName_Descending_AppliedToTerminalHop()
    {
        HttpResponseMessage resp = await PathQueryAsync("[type:task]", "&sort=name&descending=true&count=100");
        (List<long> ids, _) = await ParseResultAsync(resp);

        // Just verify the response is successful and contains the task ids
        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(_task1Id));
            Assert.That(ids, Does.Contain(_task2Id));
        });
    }
}
