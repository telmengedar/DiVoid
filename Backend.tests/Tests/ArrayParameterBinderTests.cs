using mamgo.services.Binding;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.AspNetCore.Routing;

namespace Backend.tests.Tests;

/// <summary>
/// Tests for <see cref="ArrayParameterBinder"/>.
///
/// We exercise the binder by wiring it to a real <see cref="DefaultModelBindingContext"/>
/// backed by a <see cref="QueryStringValueProvider"/> so that the full value-parsing
/// path runs — including the comma / brace / bracket / repeated-value branches.
/// </summary>
[TestFixture]
public class ArrayParameterBinderTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Runs the binder against a query-string value for a given array element type
    /// and returns the bound result (or null if binding did not succeed).
    /// </summary>
    static async Task<Array?> Bind<TElement>(string queryKey, string queryValue)
    {
        return await BindRaw(queryKey, new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            [queryKey] = queryValue
        }), typeof(TElement[]));
    }

    /// <summary>Bind with multiple repeated values for the same key.</summary>
    static async Task<Array?> BindMultiple<TElement>(string queryKey, params string[] values)
    {
        return await BindRaw(queryKey, new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            [queryKey] = new Microsoft.Extensions.Primitives.StringValues(values)
        }), typeof(TElement[]));
    }

    static async Task<Array?> BindRaw(string queryKey, IQueryCollection queryCollection, Type arrayType)
    {
        ArrayParameterBinder binder = new();

        ActionContext actionContext = new(
            new DefaultHttpContext(),
            new RouteData(),
            new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());

        ModelMetadataProvider metadataProvider = new EmptyModelMetadataProvider();
        ModelMetadata metadata = metadataProvider.GetMetadataForType(arrayType);

        DefaultModelBindingContext ctx = new()
        {
            ModelName = queryKey,
            ModelMetadata = metadata,
            ModelState = new ModelStateDictionary(),
            ValueProvider = new QueryStringValueProvider(
                BindingSource.Query,
                queryCollection,
                System.Globalization.CultureInfo.InvariantCulture),
            ActionContext = actionContext,
        };

        await binder.BindModelAsync(ctx);
        return ctx.Result.IsModelSet ? (Array?)ctx.Result.Model : null;
    }

    // -----------------------------------------------------------------------
    // long[] parsing
    // -----------------------------------------------------------------------

    [Test]
    public async Task Bind_SingleLong_ReturnsArrayOfOne()
    {
        Array? result = await Bind<long>("id", "42");
        Assert.That(result, Is.EqualTo(new long[] { 42L }));
    }

    [Test]
    public async Task Bind_CommaSeparatedLongs_ReturnsCorrectArray()
    {
        Array? result = await Bind<long>("id", "1,2,3");
        Assert.That(result, Is.EqualTo(new long[] { 1L, 2L, 3L }));
    }

    [Test]
    public async Task Bind_BraceDelimitedLongs_ReturnsCorrectArray()
    {
        Array? result = await Bind<long>("id", "{1,2,3}");
        Assert.That(result, Is.EqualTo(new long[] { 1L, 2L, 3L }));
    }

    [Test]
    public async Task Bind_BracketDelimitedLongs_ReturnsCorrectArray()
    {
        Array? result = await Bind<long>("id", "[1,2,3]");
        Assert.That(result, Is.EqualTo(new long[] { 1L, 2L, 3L }));
    }

    [Test]
    public async Task Bind_RepeatedLongValues_ReturnsCorrectArray()
    {
        Array? result = await BindMultiple<long>("id", "10", "20", "30");
        Assert.That(result, Is.EqualTo(new long[] { 10L, 20L, 30L }));
    }

    // -----------------------------------------------------------------------
    // string[] parsing
    // -----------------------------------------------------------------------

    [Test]
    public async Task Bind_SingleString_ReturnsArrayOfOne()
    {
        Array? result = await Bind<string>("type", "documentation");
        Assert.That(result, Is.EqualTo(new[] { "documentation" }));
    }

    [Test]
    public async Task Bind_CommaSeparatedStrings_ReturnsCorrectArray()
    {
        Array? result = await Bind<string>("type", "task,documentation");
        Assert.That(result, Is.EqualTo(new[] { "task", "documentation" }));
    }

    [Test]
    public async Task Bind_BraceDelimitedStrings_ReturnsCorrectArray()
    {
        Array? result = await Bind<string>("type", "{task,documentation}");
        Assert.That(result, Is.EqualTo(new[] { "task", "documentation" }));
    }

    [Test]
    public async Task Bind_BracketDelimitedStrings_ReturnsCorrectArray()
    {
        Array? result = await Bind<string>("type", "[task,documentation]");
        Assert.That(result, Is.EqualTo(new[] { "task", "documentation" }));
    }

    [Test]
    public async Task Bind_RepeatedStringValues_ReturnsCorrectArray()
    {
        Array? result = await BindMultiple<string>("type", "task", "documentation");
        Assert.That(result, Is.EqualTo(new[] { "task", "documentation" }));
    }

    // -----------------------------------------------------------------------
    // Empty / missing input
    // -----------------------------------------------------------------------

    [Test]
    public async Task Bind_MissingKey_ReturnsNull()
    {
        // Query has a different key than what we bind to — no result expected.
        ArrayParameterBinder binder = new();
        ActionContext actionContext = new(
            new DefaultHttpContext(),
            new RouteData(),
            new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());

        ModelMetadataProvider metadataProvider = new EmptyModelMetadataProvider();
        ModelMetadata metadata = metadataProvider.GetMetadataForType(typeof(long[]));

        DefaultModelBindingContext ctx = new()
        {
            ModelName = "id",
            ModelMetadata = metadata,
            ModelState = new ModelStateDictionary(),
            ValueProvider = new QueryStringValueProvider(
                BindingSource.Query,
                new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
                {
                    ["other"] = "5"
                }),
                System.Globalization.CultureInfo.InvariantCulture),
            ActionContext = actionContext,
        };

        await binder.BindModelAsync(ctx);
        Assert.That(ctx.Result.IsModelSet, Is.False);
    }

    [Test]
    public async Task Bind_EmptyStringValue_ReturnsNull()
    {
        Array? result = await Bind<long>("id", "");
        Assert.That(result, Is.Null);
    }
}
