using System;
using System.Linq;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Errors;
using Pooshit.Http;
using Pooshit.Json;

namespace Backend.tests.Tests;

/// <summary>
/// HTTP-layer tests driving the <c>refinement</c>/<c>norefinement</c>, <c>status</c>/<c>nostatus</c>
/// and <c>severity</c>/<c>noseverity</c> query-string flags and the <c>excludeId</c>/<c>excludeName</c> exclusions through <c>GET /api/nodes</c>,
/// plus the comma, repeated-key, bracket and brace array syntaxes for the <c>severity</c> and <c>id</c> filters.
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

    async Task<long> CreateNamedNodeAsync(string? name)
    {
        NodeDetails created = await http.Post<NodeDetails, NodeDetails>(
            $"{TestSetup.BaseUrl}/api/nodes",
            new NodeDetails { Type = "task", Name = name },
            new HttpOptions());
        return created.Id;
    }

    async Task<Page<NodeDetails>> ListPageAsync(string query)
    {
        HttpResponseMessage response = await http.Get<HttpResponseMessage>($"{TestSetup.BaseUrl}/api/nodes?{query}");
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync();
        return Json.Read<Page<NodeDetails>>(json);
    }

    static string Scope(params long[] ids) => $"id={string.Join(",", ids)}&count=100";

    static string Escaped(string value) => Uri.EscapeDataString(value);

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

    [Test, Parallelizable]
    public async Task ExcludeId_OmitsListedNodesKeepsOthers()
    {
        string tag = NewTag();
        long first = await CreateNamedNodeAsync($"{tag}-1");
        long second = await CreateNamedNodeAsync($"{tag}-2");
        long third = await CreateNamedNodeAsync($"{tag}-3");

        Page<NodeDetails> page = await ListPageAsync($"{Scope(first, second, third)}&excludeId={second}");

        long[] ids = page.Result.Select(n => n.Id).ToArray();
        Assert.Multiple(() => {
            Assert.That(ids, Does.Not.Contain(second), "listed id must be omitted");
            Assert.That(ids, Does.Contain(first), "unlisted id before the excluded one must be returned");
            Assert.That(ids, Does.Contain(third), "unlisted id after the excluded one must be returned");
        });
    }

    [Test, Parallelizable]
    public async Task ExcludeNameExact_OmitsOnlyExactMatch()
    {
        string tag = NewTag();
        long exact = await CreateNamedNodeAsync($"{tag}-alpha");
        long longer = await CreateNamedNodeAsync($"{tag}-alpha-2");

        Page<NodeDetails> page = await ListPageAsync($"{Scope(exact, longer)}&excludeName={Escaped($"{tag}-alpha")}");

        long[] ids = page.Result.Select(n => n.Id).ToArray();
        Assert.Multiple(() => {
            Assert.That(ids, Does.Not.Contain(exact), "node named exactly like the value must be omitted");
            Assert.That(ids, Does.Contain(longer), "node whose name merely starts with the value must be returned");
        });
    }

    [Test, Parallelizable]
    public async Task ExcludeNameWildcard_OmitsPatternMatches()
    {
        string tag = NewTag();
        long runOne = await CreateNamedNodeAsync($"{tag}-run-1");
        long runTwo = await CreateNamedNodeAsync($"{tag}-run-2");
        long skip = await CreateNamedNodeAsync($"{tag}-skip-1");
        long other = await CreateNamedNodeAsync($"{tag}-other");

        Page<NodeDetails> page = await ListPageAsync($"{Scope(runOne, runTwo, skip, other)}&excludeName={Escaped($"{tag}-run-%")}&excludeName={Escaped($"{tag}-skip-%")}");

        long[] ids = page.Result.Select(n => n.Id).ToArray();
        Assert.Multiple(() => {
            Assert.That(ids, Does.Not.Contain(runOne), "first node of the first pattern must be omitted");
            Assert.That(ids, Does.Not.Contain(runTwo), "second node of the first pattern must be omitted");
            Assert.That(ids, Does.Not.Contain(skip), "node of the second pattern must be omitted");
            Assert.That(ids, Does.Contain(other), "node matching no pattern must be returned");
        });
    }

    [Test, Parallelizable]
    public async Task ExcludeNameMixedExactAndWildcard_MatchesEveryValueAsPattern()
    {
        string tag = NewTag();
        long exact = await CreateNamedNodeAsync($"{tag}-a");
        long pattern = await CreateNamedNodeAsync($"{tag}-b-1");
        long kept = await CreateNamedNodeAsync($"{tag}-keep");

        Page<NodeDetails> page = await ListPageAsync($"{Scope(exact, pattern, kept)}&excludeName={Escaped($"{tag.ToUpperInvariant()}-A")}&excludeName={Escaped($"{tag}-b-%")}");

        Assert.That(page.Result.Select(n => n.Id), Is.EqualTo(new[] { kept }));
    }

    [Test, Parallelizable]
    public async Task ExcludeNameExact_KeepsNodesWithoutName()
    {
        string tag = NewTag();
        long unnamed = await CreateNamedNodeAsync(null);
        long named = await CreateNamedNodeAsync($"{tag}-x");

        Page<NodeDetails> page = await ListPageAsync($"{Scope(unnamed, named)}&excludeName={Escaped($"{tag}-x")}");

        long[] ids = page.Result.Select(n => n.Id).ToArray();
        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(unnamed), "node without a name must survive an exact-value exclusion");
            Assert.That(ids, Does.Not.Contain(named), "node with the excluded name must be omitted");
        });
    }

    [Test, Parallelizable]
    public async Task ExcludeNameWildcard_KeepsNodesWithoutName()
    {
        string tag = NewTag();
        long unnamed = await CreateNamedNodeAsync(null);
        long named = await CreateNamedNodeAsync($"{tag}-x");

        Page<NodeDetails> page = await ListPageAsync($"{Scope(unnamed, named)}&excludeName={Escaped($"{tag}-%")}");

        long[] ids = page.Result.Select(n => n.Id).ToArray();
        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(unnamed), "node without a name must survive a wildcard exclusion");
            Assert.That(ids, Does.Not.Contain(named), "node matching the pattern must be omitted");
        });
    }

    [Test, Parallelizable]
    public async Task ExcludeName_CountFilledFromEligibleRows()
    {
        string tag = NewTag();
        long[] excluded = [await CreateNamedNodeAsync($"{tag}-run-1"), await CreateNamedNodeAsync($"{tag}-run-2"), await CreateNamedNodeAsync($"{tag}-run-3")];
        List<long> eligible = [];
        for (int i = 1; i <= 5; i++)
            eligible.Add(await CreateNamedNodeAsync($"{tag}-keep-{i}"));

        Page<NodeDetails> page = await ListPageAsync($"id={string.Join(",", excluded.Concat(eligible))}&sort=id&count=3&excludeName={Escaped($"{tag}-run-%")}");

        Assert.Multiple(() => {
            Assert.That(page.Result.Select(n => n.Id), Is.EqualTo(eligible.Take(3)), "the page must be filled with the first eligible rows");
            Assert.That(page.Total, Is.EqualTo(5), "total must count only eligible rows");
        });
    }

    [Test, Parallelizable]
    public async Task ExcludeId_WithLinkedTo_OmitsExcludedNeighbour()
    {
        string tag = NewTag();
        long anchor = await CreateNamedNodeAsync($"{tag}-anchor");
        long excluded = await CreateNamedNodeAsync($"{tag}-b");
        long kept = await CreateNamedNodeAsync($"{tag}-c");
        await http.Post<long>($"{TestSetup.BaseUrl}/api/nodes/{anchor}/links", excluded);
        await http.Post<long>($"{TestSetup.BaseUrl}/api/nodes/{anchor}/links", kept);

        Page<NodeDetails> page = await ListPageAsync($"linkedto={anchor}&excludeId={excluded}&count=100");

        Assert.That(page.Result.Select(n => n.Id), Is.EqualTo(new[] { kept }));
    }

    [TestCase("severity=3,5", TestName = "SeverityCommaSeparated_ReturnsNodesMatchingAnyListedValue")]
    [TestCase("severity=3&severity=5", TestName = "SeverityRepeatedKey_ReturnsNodesMatchingAnyListedValue")]
    [Parallelizable]
    public async Task SeverityListSyntax_ReturnsNodesMatchingAnyListedValue(string query)
    {
        string tag = NewTag();
        long first = await CreateNodeAsync(tag, severity: 3);
        long second = await CreateNodeAsync(tag, severity: 5);
        long third = await CreateNodeAsync(tag, severity: 7);

        long[] ids = await ListIdsAsync(tag, query);

        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(first), $"'{query}' must match its first integer value");
            Assert.That(ids, Does.Contain(second), $"'{query}' must match its second integer value");
            Assert.That(ids, Does.Not.Contain(third), $"integer value not listed in '{query}' must be excluded");
        });
    }

    [TestCase("id=[{0},{1}]", TestName = "IdBracketDelimited_ReturnsOnlyListedNodes")]
    [TestCase("id={{{0},{1}}}", TestName = "IdBraceDelimited_ReturnsOnlyListedNodes")]
    [TestCase("id={0}&id={1}", TestName = "IdRepeatedKey_ReturnsOnlyListedNodes")]
    [Parallelizable]
    public async Task IdListSyntax_ReturnsOnlyListedNodes(string queryTemplate)
    {
        string tag = NewTag();
        long first = await CreateNodeAsync(tag);
        long second = await CreateNodeAsync(tag);
        long third = await CreateNodeAsync(tag);
        string query = string.Format(queryTemplate, first, second);

        long[] ids = await ListIdsAsync(tag, query);

        Assert.Multiple(() => {
            Assert.That(ids, Does.Contain(first), $"'{query}' must match its first id");
            Assert.That(ids, Does.Contain(second), $"'{query}' must match its second id");
            Assert.That(ids, Does.Not.Contain(third), $"id not listed in '{query}' must be excluded");
        });
    }


    async Task<(int Status, ErrorResponse Error)> GetErrorAsync(string query)
    {
        HttpResponseMessage response = await http.Get<HttpResponseMessage>($"{TestSetup.BaseUrl}/api/nodes?{query}");
        string json = await response.Content.ReadAsStringAsync();
        return ((int) response.StatusCode, Json.Read<ErrorResponse>(json));
    }


    [TestCase("severity=abc", "abc", TestName = "SeverityCommaSeparated_UnparsableValue_Returns400BadParameter")]
    [TestCase("severity=[3,5", "[3", TestName = "SeverityUnbalancedBracket_Returns400BadParameter")]
    [TestCase("severity=3&severity=x", "x", TestName = "SeverityRepeatedKey_UnparsableValue_Returns400BadParameter")]
    [Parallelizable]
    public async Task SeverityListSyntax_UnparsableValue_Returns400BadParameter(string query, string token)
    {
        (int status, ErrorResponse error) = await GetErrorAsync(query);

        Assert.Multiple(() => {
            Assert.That(status, Is.EqualTo(400), $"'{query}' is malformed client input");
            Assert.That(error.Code, Is.EqualTo("badparameter"), "error body must be the canonical shape");
            Assert.That(error.Text, Does.Contain($"'{token}'"), "error text must name the offending token");
            Assert.That(error.Text, Does.EndWith(" severity").IgnoreCase, "error text must name the parameter");
        });
    }


    [TestCase("id={1,2]", "{1", TestName = "IdMismatchedDelimiters_Returns400BadParameter")]
    [TestCase("id=abc", "abc", TestName = "IdCommaSeparated_UnparsableValue_Returns400BadParameter")]
    [TestCase("id=99999999999999999999", "99999999999999999999", TestName = "IdOverflow_Returns400BadParameter")]
    [Parallelizable]
    public async Task IdListSyntax_UnparsableValue_Returns400BadParameter(string query, string token)
    {
        (int status, ErrorResponse error) = await GetErrorAsync(query);

        Assert.Multiple(() => {
            Assert.That(status, Is.EqualTo(400), $"'{query}' is malformed client input");
            Assert.That(error.Code, Is.EqualTo("badparameter"), "error body must be the canonical shape");
            Assert.That(error.Text, Does.Contain($"'{token}'"), "error text must name the offending token");
            Assert.That(error.Text, Does.EndWith(" id").IgnoreCase, "error text must name the parameter");
        });
    }
}
