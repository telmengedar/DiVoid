using Backend.Models.Auth;
using Backend.Models.Nodes;
using Backend.Models.Organizations;
using Backend.Models.Users;
using Backend.Services.Organizations;
using Microsoft.Extensions.DependencyInjection;
using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Http;
using Pooshit.Http.Paths;
using Pooshit.Ocelot.Entities;

namespace Backend.tests.Fixtures;

/// <summary>An isolated authenticated organization test environment.</summary>
public sealed class OrganizationTestEnvironment : IDisposable {
    /// <summary>The isolated graph fixture.</summary>
    public NodeChangeFeedFixture Graph { get; }

    /// <summary>The organization administrator service.</summary>
    public IOrganizationService Organizations => Graph.Host.Services.GetRequiredService<IOrganizationService>();

    /// <summary>The isolated database.</summary>
    public IEntityManager Database => Graph.Database;

    /// <summary>The synthetic first organization.</summary>
    public long OrganizationId { get; private set; }

    /// <summary>The synthetic second organization.</summary>
    public long OtherOrganizationId { get; private set; }

    OrganizationTestEnvironment(NodeChangeFeedFixture graph) => Graph = graph;

    /// <summary>Creates a fresh environment.</summary>
    /// <param name="postgres">Whether PostgreSQL is selected.</param>
    /// <returns>The environment.</returns>
    public static async Task<OrganizationTestEnvironment> Create(bool postgres = false) {
        OrganizationTestEnvironment environment = new(await NodeChangeFeedFixture.CreateAsync(postgres));
        environment.OrganizationId = (await environment.Organizations.Create(new Organization { Name = "first" })).Id;
        environment.OtherOrganizationId = (await environment.Organizations.Create(new Organization { Name = "second" })).Id;
        await environment.Assign(environment.Graph.OwnerId, environment.OrganizationId);
        return environment;
    }

    /// <summary>Assigns persisted membership through the supported producer.</summary>
    /// <param name="user">The synthetic user identifier.</param>
    /// <param name="organization">The optional organization.</param>
    /// <returns>The asynchronous operation.</returns>
    public Task<UserDetails> Assign(long user, long? organization) => Graph.Users.UpdateUser(user, Patch("/organizationId", organization));

    /// <summary>Adds a synthetic user.</summary>
    /// <param name="organization">The optional organization.</param>
    /// <param name="permissions">The persisted permission array.</param>
    /// <returns>The synthetic user identifier.</returns>
    public async Task<long> User(long? organization, string permissions = "[\"read\",\"write\"]") {
        long id = await Graph.AddUserAsync(permissions);
        await Assign(id, organization);
        return id;
    }

    /// <summary>Builds finite-request options for a synthetic principal.</summary>
    /// <param name="user">The user identifier.</param>
    /// <param name="jwt">Whether a JWT is selected.</param>
    /// <param name="permissions">The optional key permissions.</param>
    /// <returns>The authentication options.</returns>
    public async Task<HttpOptions> Options(long user, bool jwt = true, string[]? permissions = null) {
        string token = jwt ? Graph.Host.MintToken(userId: user) : (await Graph.AddKeyAsync(user, permissions ?? ["read", "write"])).PlaintextKey;
        return new HttpOptions { TokenProvider = new NodeChangeTokenProvider(token) };
    }

    /// <summary>Reads an endpoint through the actual HTTP pipeline.</summary>
    /// <param name="options">The synthetic authentication.</param>
    /// <param name="path">The resource segments under api.</param>
    /// <returns>The HTTP response.</returns>
    public Task<HttpResponseMessage> Get(HttpOptions options, params string[] path) => Graph.HttpService().Get<HttpResponseMessage>(Url(path), options);

    /// <summary>Posts an endpoint through the actual HTTP pipeline.</summary>
    /// <param name="options">The synthetic authentication.</param>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="body">The request payload.</param>
    /// <param name="path">The resource segments under api.</param>
    /// <returns>The HTTP response.</returns>
    public Task<HttpResponseMessage> Post<T>(HttpOptions options, T body, params string[] path) => Graph.HttpService().Post<T, HttpResponseMessage>(Url(path), body, options);

    /// <summary>Posts exact synthetic JSON through the HTTP service.</summary>
    /// <param name="options">The synthetic authentication.</param>
    /// <param name="json">The exact request JSON.</param>
    /// <param name="path">The resource segments under api.</param>
    /// <returns>The HTTP response.</returns>
    public async Task<HttpResponseMessage> PostJson(HttpOptions options, string json, params string[] path) {
        IHttpService http = Graph.HttpService();
        using HttpRequestMessage request = await http.CreateRequest(Url(path), HttpMethod.Post, options);
        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        return await http.Send<HttpResponseMessage>(request, options);
    }

    /// <summary>Patches an endpoint through the actual HTTP pipeline.</summary>
    /// <param name="options">The synthetic authentication.</param>
    /// <param name="patches">The requested changes.</param>
    /// <param name="path">The resource segments under api.</param>
    /// <returns>The HTTP response.</returns>
    public Task<HttpResponseMessage> PatchRequest(HttpOptions options, PatchOperation[] patches, params string[] path) => Graph.HttpService().Patch<PatchOperation[], HttpResponseMessage>(Url(path), patches, options);

    /// <summary>Builds an API URL.</summary>
    /// <param name="path">The resource segments under api.</param>
    /// <returns>The URL.</returns>
    public static string Url(params string[] path) => Rest.Path(new[] { TestSetup.BaseUrl, "api" }.Concat(path).ToArray());

    /// <summary>Builds a replacement operation.</summary>
    /// <param name="path">The field path.</param>
    /// <param name="value">The replacement value.</param>
    /// <returns>The patch operation.</returns>
    public static PatchOperation Patch(string path, object? value) => new() { Op = "replace", Path = path, Value = value };

    /// <inheritdoc />
    public void Dispose() => Graph.Dispose();
}
