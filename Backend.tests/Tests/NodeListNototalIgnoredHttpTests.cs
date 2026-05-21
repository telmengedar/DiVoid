using System.Collections.Generic;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.Http;
using Pooshit.Json;

namespace Backend.tests.Tests;

/// <summary>
/// Regression test for DiVoid #779: the legacy <c>nototal</c> query parameter is
/// silently ignored. Callers that still send <c>?nototal=true</c> must receive the
/// same response — including a valid <c>total</c> count — as callers that omit it.
/// </summary>
[TestFixture]
public class NodeListNototalIgnoredHttpTests
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

    async Task<long> CreateNodeAsync(string name)
    {
        NodeDetails created = await http.Post<NodeDetails, NodeDetails>(
            $"{TestSetup.BaseUrl}/api/nodes",
            new NodeDetails { Type = "task", Name = name },
            new HttpOptions());
        return created.Id;
    }

    static async Task<Page<NodeDetails>> ReadPageAsync(System.Net.Http.HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync();
        return Json.Read<Page<NodeDetails>>(json);
    }

    [Test]
    public async Task List_NototalTrue_SameTotalAsWithoutParam()
    {
        await CreateNodeAsync("NototalNode1");
        await CreateNodeAsync("NototalNode2");

        System.Net.Http.HttpResponseMessage withParam =
            await http.Get<System.Net.Http.HttpResponseMessage>($"{TestSetup.BaseUrl}/api/nodes?count=5&nototal=true");
        System.Net.Http.HttpResponseMessage withoutParam =
            await http.Get<System.Net.Http.HttpResponseMessage>($"{TestSetup.BaseUrl}/api/nodes?count=5");

        Page<NodeDetails> pageWith = await ReadPageAsync(withParam);
        Page<NodeDetails> pageWithout = await ReadPageAsync(withoutParam);

        Assert.That(pageWith.Total, Is.EqualTo(pageWithout.Total),
            "nototal=true must be silently ignored: total must match the baseline response");
        Assert.That(pageWith.Total, Is.GreaterThan(0),
            "total must be a real count, not suppressed or zero");
    }

    [Test]
    public async Task List_NototalFalse_SameTotalAsWithoutParam()
    {
        System.Net.Http.HttpResponseMessage withParam =
            await http.Get<System.Net.Http.HttpResponseMessage>($"{TestSetup.BaseUrl}/api/nodes?count=5&nototal=false");
        System.Net.Http.HttpResponseMessage withoutParam =
            await http.Get<System.Net.Http.HttpResponseMessage>($"{TestSetup.BaseUrl}/api/nodes?count=5");

        Page<NodeDetails> pageWith = await ReadPageAsync(withParam);
        Page<NodeDetails> pageWithout = await ReadPageAsync(withoutParam);

        Assert.That(pageWith.Total, Is.EqualTo(pageWithout.Total),
            "nototal=false must also be silently ignored");
    }
}
