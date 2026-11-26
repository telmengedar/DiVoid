using System.Net;
using Backend.Models.Nodes;
using Backend.Models.Users;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.AspNetCore.Services.Errors.Exceptions;
using Pooshit.Http;
using Pooshit.Http.Paths;
using Pooshit.Json;
using Pooshit.Ocelot.Tokens;
using static Backend.tests.Fixtures.OrganizationTestEnvironment;

namespace Backend.tests.Tests;

/// <summary>Checks organization access at graph disclosure and mutation boundaries.</summary>
[TestFixture, Parallelizable]
public class OrganizationBoundaryTests {
    /// <summary>Checks related-node and traversal surfaces never expand hidden endpoints.</summary>
    /// <param name="postgres">Whether PostgreSQL is selected.</param>
    /// <returns>The asynchronous operation.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task Graph_HiddenEndpoints_AreAbsentFromTraversalAdjacencyCountsAndLookup(bool postgres) {
        using OrganizationTestEnvironment environment = await OrganizationTestEnvironment.Create(postgres);
        long reader = await environment.User(environment.OrganizationId);
        long other = await environment.User(environment.OtherOrganizationId);
        HttpOptions options = await environment.Options(reader);
        NodeDetails visible = await environment.Graph.Nodes.CreateNode(new NodeDetails { Type = "visible", Name = "visible", Access = NodeAccess.OrgRead }, environment.Graph.OwnerId);
        NodeDetails hidden = await environment.Graph.Nodes.CreateNode(new NodeDetails { Type = "hidden-only", Name = "hidden", Access = NodeAccess.OrgRead }, other);
        NodeDetails end = await environment.Graph.Nodes.CreateNode(new NodeDetails { Type = "end", Name = "end", Access = NodeAccess.Read }, other);
        await environment.Graph.Nodes.LinkNodes(visible.Id, hidden.Id, environment.Graph.OwnerId, true, LinkType.Unidirectional, "hidden-context");
        await environment.Graph.Nodes.LinkNodes(hidden.Id, end.Id, environment.Graph.OwnerId, true);
        await environment.Graph.Nodes.LinkNodes(visible.Id, end.Id, environment.Graph.OwnerId, true);
        await environment.Graph.Users.UpdateUser(other, Patch("/homeNodeId", hidden.Id));
        QueryParameters query = new("id", string.Join(',', visible.Id, hidden.Id, end.Id));
        query.Add("fields", "id,name,links,linkDetails");
        using HttpResponseMessage listed = await environment.Graph.HttpService().Get<HttpResponseMessage>(Rest.PathQuery(query, TestSetup.BaseUrl, "api", "nodes"), options);
        Assert.That(listed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Page<NodeDetails> page = Json.Read<Page<NodeDetails>>(await listed.Content.ReadAsStringAsync());
        Assert.That(page.Result.Select(n => n.Id), Is.EquivalentTo(new[] { visible.Id, end.Id }));
        NodeDetails annotated = page.Result.Single(n => n.Id == visible.Id);
        Assert.That(annotated.Links, Is.EqualTo(new[] { end.Id }));
        Assert.That(annotated.LinkDetails, Has.Length.EqualTo(1));
        Assert.That(annotated.LinkDetails[0].TargetId, Is.EqualTo(end.Id));
        QueryParameters edges = new("ids", visible.Id);
        using HttpResponseMessage links = await environment.Graph.HttpService().Get<HttpResponseMessage>(Rest.PathQuery(edges, TestSetup.BaseUrl, "api", "nodes", "links"), options);
        Page<NodeLink> linkPage = Json.Read<Page<NodeLink>>(await links.Content.ReadAsStringAsync());
        Assert.That(linkPage.Result, Has.Length.EqualTo(1));
        Assert.That(linkPage.Result[0].TargetId, Is.EqualTo(end.Id));
        QueryParameters path = new("path", $"[id:{visible.Id}]/[id:{hidden.Id}]/[id:{end.Id}]");
        using HttpResponseMessage traversed = await environment.Graph.HttpService().Get<HttpResponseMessage>(Rest.PathQuery(path, TestSetup.BaseUrl, "api", "nodes"), options);
        Assert.That(traversed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(Json.Read<Page<NodeDetails>>(await traversed.Content.ReadAsStringAsync()).Result, Is.Empty);
        QueryParameters linked = new("linkedto", hidden.Id);
        using HttpResponseMessage seeded = await environment.Graph.HttpService().Get<HttpResponseMessage>(Rest.PathQuery(linked, TestSetup.BaseUrl, "api", "nodes"), options);
        Assert.That(Json.Read<Page<NodeDetails>>(await seeded.Content.ReadAsStringAsync()).Result, Is.Empty);
        using HttpResponseMessage lookup = await environment.Get(options, "nodes", hidden.Id.ToString(), "user");
        Assert.That(lookup.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        using HttpResponseMessage types = await environment.Get(options, "types");
        Assert.That(types.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Page<TypeListItem> typePage = Json.Read<Page<TypeListItem>>(await types.Content.ReadAsStringAsync());
        Assert.That(typePage.Result.Single(t => t.Type == "visible").Count, Is.EqualTo(1));
        Assert.That(typePage.Result.Select(t => t.Type), Does.Not.Contain("hidden-only"));
    }

    /// <summary>Checks denied target mutations and content operations preserve stored state.</summary>
    /// <param name="postgres">Whether PostgreSQL is selected.</param>
    /// <returns>The asynchronous operation.</returns>
    [TestCase(false), TestCase(true), Parallelizable]
    public async Task Mutations_OrganizationScopeAndHiddenTargets_AreAtomic(bool postgres) {
        using OrganizationTestEnvironment environment = await OrganizationTestEnvironment.Create(postgres);
        long writer = await environment.User(environment.OrganizationId);
        long other = await environment.User(environment.OtherOrganizationId);
        NodeDetails writable = await environment.Graph.Nodes.CreateNode(new NodeDetails { Name = "writable", Access = NodeAccess.OrgRead | NodeAccess.OrgWrite }, environment.Graph.OwnerId);
        NodeDetails hidden = await environment.Graph.Nodes.CreateNode(new NodeDetails { Name = "hidden", Access = NodeAccess.OrgRead | NodeAccess.OrgWrite }, other);
        await environment.Graph.Nodes.Patch(writable.Id, [Patch("/name", "changed")], writer, false, default);
        Assert.That((await environment.Graph.Nodes.GetNodeById(writable.Id, writer, false)).Name, Is.EqualTo("changed"));
        using MemoryStream content = new("original"u8.ToArray());
        await environment.Graph.Nodes.UploadContent(writable.Id, "text/plain", content, writer, false);
        await environment.Graph.Nodes.PatchContent(writable.Id, [new ContentEdit { Unit = ContentEditUnit.Char, Start = 0, Length = 8, Value = "updated" }], writer, false, default);
        (string _, Stream read) = await environment.Graph.Nodes.GetNodeData(writable.Id, writer, false);
        using (read) {
            using StreamReader text = new(read);
            Assert.That(await text.ReadToEndAsync(), Is.EqualTo("updated"));
        }
        Assert.ThrowsAsync<NotFoundException<Node>>(() => environment.Graph.Nodes.GetNodeById(hidden.Id, writer, false));
        Assert.ThrowsAsync<NotFoundException<Node>>(() => environment.Graph.Nodes.GetNodeData(hidden.Id, writer, false));
        Assert.ThrowsAsync<NotFoundException<Node>>(() => environment.Graph.Nodes.Patch(hidden.Id, [Patch("/name", "forged")], writer, false, default));
        Assert.ThrowsAsync<NotFoundException<Node>>(() => environment.Graph.Nodes.UploadContent(hidden.Id, "text/plain", new MemoryStream("forged"u8.ToArray()), writer, false));
        Assert.ThrowsAsync<NotFoundException<Node>>(() => environment.Graph.Nodes.Delete(hidden.Id, writer, false));
        long before = await environment.Database.Load<Node>(DB.Count()).ExecuteScalarAsync<long>();
        Assert.ThrowsAsync<NotFoundException<Node>>(() => environment.Graph.Nodes.CreateNode(new NodeDetails { Name = "partial", Links = [writable.Id, hidden.Id] }, writer));
        Assert.That(await environment.Database.Load<Node>(DB.Count()).ExecuteScalarAsync<long>(), Is.EqualTo(before));
        Assert.ThrowsAsync<NotFoundException<Node>>(() => environment.Graph.Nodes.LinkNodes(writable.Id, hidden.Id, writer, false));
        await environment.Graph.Nodes.LinkNodes(writable.Id, hidden.Id, environment.Graph.OwnerId, true);
        Assert.ThrowsAsync<NotFoundException<Node>>(() => environment.Graph.Nodes.UnlinkNodes(writable.Id, hidden.Id, writer, false));
        Assert.ThrowsAsync<NotFoundException<Node>>(() => environment.Graph.Nodes.PatchLink(writable.Id, hidden.Id, [Patch("/context", "forged")], writer, false, default));
        Assert.That(await environment.Database.Load<NodeLink>(DB.Count()).Where(l => l.SourceId == writable.Id && l.TargetId == hidden.Id).ExecuteScalarAsync<long>(), Is.EqualTo(1));
        await environment.Assign(writer, environment.OtherOrganizationId);
        Assert.ThrowsAsync<NotFoundException<Node>>(() => environment.Graph.Nodes.Patch(writable.Id, [Patch("/name", "stale-membership")], writer, false, default));
        await environment.Graph.Nodes.Delete(hidden.Id, writer, false);
        Assert.That(await environment.Database.Load<Node>(DB.Count()).Where(n => n.Id == hidden.Id).ExecuteScalarAsync<long>(), Is.Zero);
    }

    /// <summary>Checks real PostgreSQL ranking and paging exclude a stronger hidden result.</summary>
    /// <returns>The asynchronous operation.</returns>
    [Test, Parallelizable]
    public async Task SemanticSearch_HiddenBestMatch_IsExcludedBeforeRankAndPagination() {
        using OrganizationTestEnvironment environment = await OrganizationTestEnvironment.Create(true);
        long reader = await environment.User(environment.OrganizationId);
        long other = await environment.User(environment.OtherOrganizationId);
        NodeService semantic = new(environment.Database, new EmbeddingCapability(true), EmbeddingProducers.Function, environment.Graph.Feed);
        NodeDetails hidden = await semantic.CreateNode(new NodeDetails { Type = "secret", Name = "target query", Access = NodeAccess.OrgRead }, other);
        NodeDetails visible = await semantic.CreateNode(new NodeDetails { Type = "public", Name = "different visible query", Access = NodeAccess.Read }, environment.Graph.OwnerId);
        AsyncPageResponseWriter<NodeDetails> writer = await semantic.ListPaged(new NodeFilter { Query = "target query", Count = 1 }, reader, false);
        using MemoryStream buffer = new();
        await writer.Write(buffer);
        Page<NodeDetails> page = Json.Read<Page<NodeDetails>>(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
        Assert.That(page.Result, Has.Length.EqualTo(1));
        Assert.That(page.Result[0].Id, Is.EqualTo(visible.Id));
        Assert.That(page.Result[0].Similarity, Is.Not.Null);
        Assert.That(page.Total, Is.EqualTo(1));
        Assert.That(page.Result.Select(n => n.Id), Does.Not.Contain(hidden.Id));
    }
}
