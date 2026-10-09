using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Backend.Services.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Pooshit.Http;

namespace Backend.tests.Tests;

/// <summary>
/// HTTP tests of which <see cref="NodeFilter"/> the controller binds from array query parameters.
/// </summary>
[TestFixture, Parallelizable]
public class NodeQueriesBindingHttpTests
{
    /// <summary>
    /// records the filter handed to the list operations, then forwards to the real service
    /// </summary>
    public class FilterSpy : DispatchProxy
    {
        public INodeService Target { get; set; } = null!;

        public ConcurrentQueue<NodeFilter> Recorded { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name is nameof(INodeService.ListPaged) or nameof(INodeService.ListPagedByPath))
                Recorded.Enqueue((NodeFilter)args![0]!);
            try
            {
                return targetMethod.Invoke(Target, args);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
    }

    /// <summary>
    /// sends one GET to a private test server and returns the filter its controller bound
    /// </summary>
    static async Task<NodeFilter> BoundFilterAsync(string pathAndQuery)
    {
        ConcurrentQueue<NodeFilter> recorded = new();
        using WebApplicationFactory<Program> factory = TestSetup.CreateTestFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services => {
            ServiceDescriptor original = services.Single(d => d.ServiceType == typeof(INodeService));
            services.Remove(original);
            services.AddTransient(sp => {
                INodeService spy = DispatchProxy.Create<INodeService, FilterSpy>();
                FilterSpy proxy = (FilterSpy)spy;
                proxy.Target = (INodeService)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!);
                proxy.Recorded = recorded;
                return spy;
            });
        }));
        IHttpService http = TestSetup.HttpServiceFor(factory);

        using HttpResponseMessage response = await http.Get<HttpResponseMessage>($"{TestSetup.BaseUrl}{pathAndQuery}");

        return recorded.Single();
    }

    static string Esc(string value) => System.Uri.EscapeDataString(value);

    [Test, Parallelizable]
    [Description("DiVoid #16138: repeated queries keys bind as separate queries")]
    public async Task RepeatedQueriesKeys_BindAsSeparateQueries()
    {
        NodeFilter filter = await BoundFilterAsync("/api/nodes?queries=one&queries=two&queries=three&count=1");

        Assert.That(filter.Queries, Is.EqualTo(new[] { "one", "two", "three" }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16138: a single queries value containing commas stays one query")]
    public async Task SingleQueriesValueWithCommas_BindsAsOneQuery()
    {
        NodeFilter filter = await BoundFilterAsync($"/api/nodes?queries={Esc("how does X, Y work, really")}&count=1");

        Assert.That(filter.Queries, Is.EqualTo(new[] { "how does X, Y work, really" }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16138: the path endpoint binds queries the same way")]
    public async Task PathFilter_SingleQueriesValueWithCommas_BindsAsOneQuery()
    {
        NodeFilter filter = await BoundFilterAsync($"/api/nodes?path={Esc("[type:task]")}&queries={Esc("x, y, z")}&count=1");

        Assert.Multiple(() => {
            Assert.That(filter, Is.TypeOf<NodePathFilter>());
            Assert.That(filter.Queries, Is.EqualTo(new[] { "x, y, z" }));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16138: other array parameters keep splitting a single value on commas")]
    public async Task OtherArrayParameters_StillSplitOnCommas()
    {
        NodeFilter filter = await BoundFilterAsync("/api/nodes?name=a,b&type=task,bug&id=1,2,3&count=1");

        Assert.Multiple(() => {
            Assert.That(filter.Name, Is.EqualTo(new[] { "a", "b" }));
            Assert.That(filter.Type, Is.EqualTo(new[] { "task", "bug" }));
            Assert.That(filter.Id, Is.EqualTo(new long[] { 1, 2, 3 }));
        });
    }
}
