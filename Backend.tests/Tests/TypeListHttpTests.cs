#nullable enable
using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Backend.Models.Users;
using Backend.tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Pooshit.Json;
using Pooshit.Ocelot.Entities;

namespace Backend.tests.Tests;

/// <summary>
/// HTTP integration tests for GET /api/types (task #485).
///
/// Load-bearing cases (per DiVoid #275 and Sarah's architectural doc §14.6):
///   T1 — positive: seeded types appear with correct counts, sorted count-desc then type-asc
///   T2 — orphan-filter: a NodeType row with no referencing nodes does not appear in the response
///   T3 — empty graph: response is {"result":[],"total":0,"continue":null}
///   T4 — auth-401: missing bearer returns 401
///   T5 — auth read-policy: caller with read permission returns 200
/// </summary>
[TestFixture]
public class TypeListHttpTests
{
    JwtAuthFixture fixture = null!;
    IEntityManager db = null!;
    long readerId;
    string readerToken = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        fixture = new JwtAuthFixture();
        db = fixture.EntityManager;

        readerId = await db.Insert<User>()
                           .Columns(u => u.Name, u => u.Email, u => u.Enabled, u => u.Permissions, u => u.CreatedAt)
                           .Values("type-reader", "type-reader@test.com", true, Json.WriteString(new[] { "read" }), DateTime.UtcNow)
                           .ReturnID()
                           .ExecuteAsync();
        readerToken = fixture.MintToken(userId: readerId);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        fixture.Dispose();
    }


    HttpClient AuthClient() {
        HttpClient client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", readerToken);
        return client;
    }

    static async Task<TypeListItem[]> ReadTypesAsync(HttpResponseMessage response)
    {
        string json = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrEmpty(json))
            return [];
        JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("result", out JsonElement items))
            return [];
        if (items.ValueKind != JsonValueKind.Array)
            return [];
        JsonSerializerOptions opts = new() { PropertyNameCaseInsensitive = true };
        return items.Deserialize<TypeListItem[]>(opts) ?? [];
    }

    static async Task<long> ReadTotalAsync(HttpResponseMessage response)
    {
        string json = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrEmpty(json))
            return 0;
        JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("total", out JsonElement total))
            return 0;
        return total.GetInt64();
    }

    async Task<long> InsertTypeAsync(string typeName)
    {
        return await db.Insert<NodeType>()
                       .Columns(t => t.Type)
                       .Values(typeName)
                       .ReturnID()
                       .ExecuteAsync();
    }

    async Task InsertNodeForTypeAsync(long typeId, string name)
    {
        await db.Insert<Node>()
                .Columns(n => n.TypeId, n => n.Name, n => n.X, n => n.Y)
                .Values(typeId, name, 0.0, 0.0)
                .ExecuteAsync();
    }


    // -----------------------------------------------------------------------
    // T1 — positive: seeded types appear with correct counts, sorted count-desc
    //                then type-asc for same-count types
    // -----------------------------------------------------------------------

    [Test]
    public async Task T1_ListTypes_ReturnsSortedTypesWithCorrectCounts()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string alphaType = $"alpha-{suffix}";
        string betaType = $"beta-{suffix}";

        long alphaId = await InsertTypeAsync(alphaType);
        long betaId = await InsertTypeAsync(betaType);

        await InsertNodeForTypeAsync(alphaId, $"alpha-node-1-{suffix}");
        await InsertNodeForTypeAsync(alphaId, $"alpha-node-2-{suffix}");
        await InsertNodeForTypeAsync(betaId, $"beta-node-1-{suffix}");

        using HttpClient client = AuthClient();
        HttpResponseMessage response = await client.GetAsync("/api/types");

        Assert.That((int)response.StatusCode, Is.EqualTo(200),
            "T1: GET /api/types must return 200");

        TypeListItem[] items = await ReadTypesAsync(response);
        TypeListItem? alpha = items.FirstOrDefault(i => i.Type == alphaType);
        TypeListItem? beta = items.FirstOrDefault(i => i.Type == betaType);

        Assert.That(alpha, Is.Not.Null, "T1: alpha type must appear in response");
        Assert.That(beta, Is.Not.Null, "T1: beta type must appear in response");

        Assert.Multiple(() => {
            Assert.That(alpha!.Count, Is.EqualTo(2),
                "T1: alpha count must equal the number of seeded alpha nodes");
            Assert.That(beta!.Count, Is.EqualTo(1),
                "T1: beta count must equal the number of seeded beta nodes");
        });

        int alphaIdx = Array.IndexOf(items, alpha);
        int betaIdx = Array.IndexOf(items, beta);
        Assert.That(alphaIdx, Is.LessThan(betaIdx),
            "T1: alpha (count=2) must appear before beta (count=1) in count-desc order");

        long total = await ReadTotalAsync(response);
        Assert.That(total, Is.GreaterThanOrEqualTo(2),
            "T1: total must be at least 2 (the two seeded types)");
    }


    // -----------------------------------------------------------------------
    // T2 — orphan-filter: NodeType with no referencing nodes does not appear
    // -----------------------------------------------------------------------

    [Test]
    public async Task T2_ListTypes_OrphanNodeType_DoesNotAppear()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string orphanType = $"orphan-{suffix}";

        await InsertTypeAsync(orphanType);

        using HttpClient client = AuthClient();
        HttpResponseMessage response = await client.GetAsync("/api/types");

        Assert.That((int)response.StatusCode, Is.EqualTo(200),
            "T2: GET /api/types must return 200");

        TypeListItem[] items = await ReadTypesAsync(response);
        TypeListItem? orphan = items.FirstOrDefault(i => i.Type == orphanType);

        Assert.That(orphan, Is.Null,
            "T2: orphan NodeType (no referencing nodes) must not appear in the response");
    }


    // -----------------------------------------------------------------------
    // T3 — empty graph: response envelope has empty result array and total=0
    //      uses a fresh JwtAuthFixture so the DB has no nodes from prior tests
    // -----------------------------------------------------------------------

    [Test]
    public async Task T3_ListTypes_EmptyGraph_ReturnsEmptyEnvelope()
    {
        using JwtAuthFixture emptyFixture = new();
        IEntityManager emptyDb = emptyFixture.EntityManager;

        long emptyUserId = await emptyDb.Insert<User>()
                                        .Columns(u => u.Name, u => u.Email, u => u.Enabled, u => u.Permissions, u => u.CreatedAt)
                                        .Values("t3-reader", "t3-reader@test.com", true, Json.WriteString(new[] { "read" }), DateTime.UtcNow)
                                        .ReturnID()
                                        .ExecuteAsync();

        string emptyToken = emptyFixture.MintToken(userId: emptyUserId);
        using HttpClient client = emptyFixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", emptyToken);

        HttpResponseMessage response = await client.GetAsync("/api/types");

        Assert.That((int)response.StatusCode, Is.EqualTo(200),
            "T3: GET /api/types on empty graph must return 200, not an error");

        TypeListItem[] items = await ReadTypesAsync(response);
        long total = await ReadTotalAsync(response);

        Assert.Multiple(() => {
            Assert.That(items, Is.Empty,
                "T3: result array must be empty for a graph with no nodes");
            Assert.That(total, Is.EqualTo(0),
                "T3: total must be 0 for a graph with no nodes");
        });
    }


    // -----------------------------------------------------------------------
    // T4 — auth-401: missing bearer returns 401
    // -----------------------------------------------------------------------

    [Test]
    public async Task T4_ListTypes_NoBearerToken_Returns401()
    {
        using HttpClient client = fixture.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/types");

        Assert.That((int)response.StatusCode, Is.EqualTo(401),
            "T4: GET /api/types without a bearer token must return 401");
    }


    // -----------------------------------------------------------------------
    // T5 — auth-read: caller with read permission returns 200
    // -----------------------------------------------------------------------

    [Test]
    public async Task T5_ListTypes_WithReadPermission_Returns200()
    {
        using HttpClient client = AuthClient();

        HttpResponseMessage response = await client.GetAsync("/api/types");

        Assert.That((int)response.StatusCode, Is.EqualTo(200),
            "T5: authenticated caller with read permission must receive 200");
    }
}
