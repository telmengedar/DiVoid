using System.Linq;
using System.Net.Http;
using System.Text.Json;
using Backend.Models.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Pooshit.Http;
using Pooshit.Json;
using Pooshit.Ocelot.Entities;

namespace Backend.tests.Tests;

/// <summary>
/// HTTP-layer tests for the <c>sort</c> vocabulary of <c>GET /api/nodes/links</c>.
/// </summary>
[TestFixture, Parallelizable]
public class NodeLinksSortHttpTests {
    static readonly string[] SortKeys = ["sourceid", "targetid", "linktype", "context"];

    WebApplicationFactory<Program> factory = null!;
    IHttpService http = null!;

    [OneTimeSetUp]
    public void Setup() {
        factory = TestSetup.CreateTestFactory();
        http = TestSetup.HttpServiceFor(factory);
    }

    [OneTimeTearDown]
    public void TearDown() {
        factory.Dispose();
    }

    async Task<long> CreateNodeAsync(string name) {
        NodeDetails created = await http.Post<NodeDetails, NodeDetails>(
            $"{TestSetup.BaseUrl}/api/nodes",
            new NodeDetails { Type = "task", Name = name },
            new HttpOptions());
        return created.Id;
    }

    async Task<long[]> CreateHubWithThreeSourcesAsync() {
        long hub = await CreateNodeAsync("LinksSortHub");
        long[] sources = [await CreateNodeAsync("LinksSortA"), await CreateNodeAsync("LinksSortB"), await CreateNodeAsync("LinksSortC")];
        foreach (long source in sources)
            await http.Post<long, HttpResponseMessage>($"{TestSetup.BaseUrl}/api/nodes/{source}/links", hub);
        return [hub, ..sources];
    }

    [TestCase("bogus")]
    [TestCase("a.b")]
    [TestCase("node.id")]
    [TestCase("sourceId.targetId.x")]
    [TestCase("x;DROP TABLE node")]
    [Parallelizable]
    public async Task ListLinks_UnknownSort_Returns400WithFieldAndAvailable(string sort) {
        long[] ids = await CreateHubWithThreeSourcesAsync();

        HttpResponseMessage response = await http.Get<HttpResponseMessage>(
            $"{TestSetup.BaseUrl}/api/nodes/links?ids={ids[0]}&sort={System.Uri.EscapeDataString(sort)}");
        string body = await response.Content.ReadAsStringAsync();

        Assert.That((int)response.StatusCode, Is.EqualTo(400), body);
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement context = doc.RootElement.GetProperty("context");
        Assert.Multiple(() => {
            Assert.That(doc.RootElement.GetProperty("code").GetString(), Is.EqualTo("badparameter"));
            Assert.That(context.GetProperty("field").GetString(), Is.EqualTo(sort));
            Assert.That(context.GetProperty("available").EnumerateArray().Select(e => e.GetString()), Is.EquivalentTo(SortKeys));
        });
    }

    [TestCase("sourceid")]
    [TestCase("sourceId")]
    [TestCase("SOURCEID")]
    [Parallelizable]
    public async Task ListLinks_SortBySourceId_OrdersRowsBothDirections(string sort) {
        long[] ids = await CreateHubWithThreeSourcesAsync();
        long[] sources = ids.Skip(1).ToArray();

        string ascending = await http.Get<string>($"{TestSetup.BaseUrl}/api/nodes/links?ids={ids[0]}&sort={sort}");
        string descending = await http.Get<string>($"{TestSetup.BaseUrl}/api/nodes/links?ids={ids[0]}&sort={sort}&descending=true");

        Assert.Multiple(() => {
            Assert.That(sources, Is.Ordered.Ascending, "scenario: the three sources have distinct ascending ids");
            Assert.That(SourceIdsOf(ascending), Is.EqualTo(sources));
            Assert.That(SourceIdsOf(descending), Is.EqualTo(sources.OrderByDescending(id => id).ToArray()));
        });
    }

    static long[] SourceIdsOf(string body)
        => Json.Read<Page<NodeLink>>(body).Result.Select(l => l.SourceId).ToArray();
}
