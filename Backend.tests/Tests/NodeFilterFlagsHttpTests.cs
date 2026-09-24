using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.Http;
using Pooshit.Json;

namespace Backend.tests.Tests;

/// <summary>
/// HTTP-layer tests driving the <c>refinement</c>/<c>norefinement</c>, <c>status</c>/<c>nostatus</c>
/// and <c>severity</c>/<c>noseverity</c> query-string flags through <c>GET /api/nodes</c>.
/// </summary>
[TestFixture, Parallelizable]
public class NodeFilterFlagsHttpTests
{
    WebApplicationFactory<Program> factory = null!;
    IHttpService http = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        factory = TestSetup.CreateTestFactory();
        http = TestSetup.HttpServiceFor(factory);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        factory.Dispose();
    }

    static string NewTag() => Guid.NewGuid().ToString("N");

    async Task<long> CreateNodeAsync(string tag, string? status = null, int? severity = null, string? refinement = null)
    {
        NodeDetails created = await http.Post<NodeDetails, NodeDetails>(
            $"{TestSetup.BaseUrl}/api/nodes",
            new NodeDetails { Type = "task", Name = tag, Status = status, Severity = severity, Refinement = refinement },
            new HttpOptions());
        return created.Id;
    }

    async Task<long[]> ListIdsAsync(string tag, string flagsQuery)
    {
        HttpResponseMessage response = await http.Get<HttpResponseMessage>(
            $"{TestSetup.BaseUrl}/api/nodes?name={tag}&count=100&{flagsQuery}");
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync();
        Page<NodeDetails> page = Json.Read<Page<NodeDetails>>(json);
        return page.Result?.Select(n => n.Id).ToArray() ?? [];
    }

    [Test, Parallelizable]
    public async Task RefinementSingleValue_ReturnsOnlyMatchingNode()
    {
        string tag = NewTag();
        long match = await CreateNodeAsync(tag, refinement: "ready");
        long other = await CreateNodeAsync(tag, refinement: "blocked");

        long[] ids = await ListIdsAsync(tag, "refinement=ready");

        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(match), "node with matching refinement must be returned");
            Assert.That(ids, Does.Not.Contain(other), "node with a different refinement must be excluded");
        });
    }

    [Test, Parallelizable]
    public async Task RefinementCommaSeparated_ReturnsNodesMatchingAnyListedValue()
    {
        string tag = NewTag();
        long alpha = await CreateNodeAsync(tag, refinement: "alpha");
        long beta = await CreateNodeAsync(tag, refinement: "beta");
        long gamma = await CreateNodeAsync(tag, refinement: "gamma");

        long[] ids = await ListIdsAsync(tag, "refinement=alpha,beta");

        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(alpha), "comma-list must match its first value");
            Assert.That(ids, Does.Contain(beta), "comma-list must match its second value");
            Assert.That(ids, Does.Not.Contain(gamma), "value not in the comma-list must be excluded");
        });
    }

    [Test, Parallelizable]
    public async Task RefinementRepeatedKey_ReturnsNodesMatchingAnyListedValue()
    {
        string tag = NewTag();
        long alpha = await CreateNodeAsync(tag, refinement: "alpha");
        long beta = await CreateNodeAsync(tag, refinement: "beta");
        long gamma = await CreateNodeAsync(tag, refinement: "gamma");

        long[] ids = await ListIdsAsync(tag, "refinement=alpha&refinement=beta");

        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(alpha), "repeated key must match its first value");
            Assert.That(ids, Does.Contain(beta), "repeated key must match its second value");
            Assert.That(ids, Does.Not.Contain(gamma), "value not among the repeated keys must be excluded");
        });
    }

    [Test, Parallelizable]
    public async Task RefinementWildcard_ReturnsNodesMatchingPattern()
    {
        string tag = NewTag();
        long work = await CreateNodeAsync(tag, refinement: "needs-work");
        long review = await CreateNodeAsync(tag, refinement: "needs-review");
        long ready = await CreateNodeAsync(tag, refinement: "ready");

        long[] ids = await ListIdsAsync(tag, $"refinement={Uri.EscapeDataString("needs-%")}");

        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(work), "wildcard must match the first prefixed value");
            Assert.That(ids, Does.Contain(review), "wildcard must match the second prefixed value");
            Assert.That(ids, Does.Not.Contain(ready), "value outside the wildcard pattern must be excluded");
        });
    }

    [Test, Parallelizable]
    public async Task NoRefinementTrue_ReturnsOnlyNodesWithNoRefinement()
    {
        string tag = NewTag();
        long withRefinement = await CreateNodeAsync(tag, refinement: "set");
        long withoutRefinement = await CreateNodeAsync(tag);

        long[] ids = await ListIdsAsync(tag, "norefinement=true");

        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(withoutRefinement), "unrefined node must be returned by norefinement=true");
            Assert.That(ids, Does.Not.Contain(withRefinement), "refined node must be excluded by norefinement=true");
        });
    }

    [Test, Parallelizable]
    public async Task RefinementValueAndNoRefinement_UsesOrComposition()
    {
        string tag = NewTag();
        long matchValue = await CreateNodeAsync(tag, refinement: "ready");
        long matchNull = await CreateNodeAsync(tag);
        long excluded = await CreateNodeAsync(tag, refinement: "other");

        long[] ids = await ListIdsAsync(tag, "refinement=ready&norefinement=true");

        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(matchValue), "node matching the refinement value must be returned");
            Assert.That(ids, Does.Contain(matchNull), "unrefined node must also be returned (OR composition)");
            Assert.That(ids, Does.Not.Contain(excluded), "node with a different refinement must stay excluded");
        });
    }

    [Test, Parallelizable]
    public async Task StatusSingleValue_ReturnsOnlyMatchingNode()
    {
        string tag = NewTag();
        long match = await CreateNodeAsync(tag, status: "open");
        long other = await CreateNodeAsync(tag, status: "closed");

        long[] ids = await ListIdsAsync(tag, "status=open");

        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(match), "node with matching status must be returned");
            Assert.That(ids, Does.Not.Contain(other), "node with a different status must be excluded");
        });
    }

    [Test, Parallelizable]
    public async Task NoStatusTrue_ReturnsOnlyNodesWithNoStatus()
    {
        string tag = NewTag();
        long withStatus = await CreateNodeAsync(tag, status: "open");
        long withoutStatus = await CreateNodeAsync(tag);

        long[] ids = await ListIdsAsync(tag, "nostatus=true");

        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(withoutStatus), "status-less node must be returned by nostatus=true");
            Assert.That(ids, Does.Not.Contain(withStatus), "node with a status must be excluded by nostatus=true");
        });
    }

    [Test, Parallelizable]
    public async Task StatusValueAndNoStatus_UsesOrComposition()
    {
        string tag = NewTag();
        long matchValue = await CreateNodeAsync(tag, status: "open");
        long matchNull = await CreateNodeAsync(tag);
        long excluded = await CreateNodeAsync(tag, status: "closed");

        long[] ids = await ListIdsAsync(tag, "status=open&nostatus=true");

        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(matchValue), "node matching the status value must be returned");
            Assert.That(ids, Does.Contain(matchNull), "status-less node must also be returned (OR composition)");
            Assert.That(ids, Does.Not.Contain(excluded), "node with a different status must stay excluded");
        });
    }

    [Test, Parallelizable]
    public async Task SeveritySingleValue_ReturnsOnlyMatchingNode()
    {
        string tag = NewTag();
        long match = await CreateNodeAsync(tag, severity: 3);
        long other = await CreateNodeAsync(tag, severity: 7);

        long[] ids = await ListIdsAsync(tag, "severity=3");

        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(match), "node with matching severity must be returned");
            Assert.That(ids, Does.Not.Contain(other), "node with a different severity must be excluded");
        });
    }

    [Test, Parallelizable]
    public async Task NoSeverityTrue_ReturnsOnlyNodesWithNoSeverity()
    {
        string tag = NewTag();
        long withSeverity = await CreateNodeAsync(tag, severity: 5);
        long withoutSeverity = await CreateNodeAsync(tag);

        long[] ids = await ListIdsAsync(tag, "noseverity=true");

        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(withoutSeverity), "severity-less node must be returned by noseverity=true");
            Assert.That(ids, Does.Not.Contain(withSeverity), "node with a severity must be excluded by noseverity=true");
        });
    }

    [Test, Parallelizable]
    public async Task SeverityValueAndNoSeverity_UsesOrComposition()
    {
        string tag = NewTag();
        long matchValue = await CreateNodeAsync(tag, severity: 5);
        long matchNull = await CreateNodeAsync(tag);
        long excluded = await CreateNodeAsync(tag, severity: 9);

        long[] ids = await ListIdsAsync(tag, "severity=5&noseverity=true");

        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(matchValue), "node matching the severity value must be returned");
            Assert.That(ids, Does.Contain(matchNull), "severity-less node must also be returned (OR composition)");
            Assert.That(ids, Does.Not.Contain(excluded), "node with a different severity must stay excluded");
        });
    }
}
