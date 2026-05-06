using Backend.Formatters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Pooshit.AspNetCore.Services.Formatters.DataStream;

namespace Backend.tests.Tests;

[TestFixture]
public class JsonStreamOutputFormatterTests
{
    // -----------------------------------------------------------------------
    // CanWriteType
    // -----------------------------------------------------------------------

    [Test]
    public void CanWriteType_IResponseWriterImplementation_ReturnsTrue()
    {
        JsonStreamOutputFormatter formatter = new();
        // AsyncPageResponseWriter<T> implements IResponseWriter
        bool result = InvokeCanWriteType(formatter, typeof(AsyncPageResponseWriter<string>));
        Assert.That(result, Is.True);
    }

    [Test]
    public void CanWriteType_IResponseWriterDirectly_ReturnsTrue()
    {
        JsonStreamOutputFormatter formatter = new();
        // The interface itself is assignable from itself
        bool result = InvokeCanWriteType(formatter, typeof(IResponseWriter));
        Assert.That(result, Is.True);
    }

    [Test]
    public void CanWriteType_PlainClass_ReturnsFalse()
    {
        JsonStreamOutputFormatter formatter = new();
        bool result = InvokeCanWriteType(formatter, typeof(string));
        Assert.That(result, Is.False);
    }

    [Test]
    public void CanWriteType_Null_ReturnsFalse()
    {
        JsonStreamOutputFormatter formatter = new();
        bool result = InvokeCanWriteType(formatter, typeof(object));
        Assert.That(result, Is.False);
    }

    // CanWriteType is protected — call via reflection.
    static bool InvokeCanWriteType(JsonStreamOutputFormatter formatter, Type type)
    {
        var method = typeof(JsonStreamOutputFormatter).GetMethod(
            "CanWriteType",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return (bool)method!.Invoke(formatter, [type])!;
    }

    // -----------------------------------------------------------------------
    // WriteResponseBodyAsync
    // -----------------------------------------------------------------------

    [Test]
    public async Task WriteResponseBodyAsync_SetsContentTypeFromWriter()
    {
        JsonStreamOutputFormatter formatter = new();

        // Build a minimal writer that writes nothing but exposes a known ContentType.
        FakeResponseWriter writer = new("text/plain");

        DefaultHttpContext httpContext = new();
        httpContext.Response.Body = new MemoryStream();

        OutputFormatterWriteContext ctx = new(
            httpContext,
            (stream, encoding) => new StreamWriter(stream, encoding),
            typeof(FakeResponseWriter),
            writer);

        await formatter.WriteResponseBodyAsync(ctx);

        Assert.That(httpContext.Response.ContentType, Is.EqualTo("text/plain"));
    }

    [Test]
    public async Task WriteResponseBodyAsync_DelegatesWriteToResponseWriter()
    {
        JsonStreamOutputFormatter formatter = new();

        byte[] expectedBytes = "hello"u8.ToArray();
        FakeResponseWriter writer = new("application/json", expectedBytes);

        DefaultHttpContext httpContext = new();
        MemoryStream responseBody = new();
        httpContext.Response.Body = responseBody;

        OutputFormatterWriteContext ctx = new(
            httpContext,
            (stream, encoding) => new StreamWriter(stream, encoding),
            typeof(FakeResponseWriter),
            writer);

        await formatter.WriteResponseBodyAsync(ctx);

        Assert.That(responseBody.ToArray(), Is.EqualTo(expectedBytes));
    }

    // -----------------------------------------------------------------------
    // Fake helpers
    // -----------------------------------------------------------------------

    sealed class FakeResponseWriter : IResponseWriter
    {
        readonly byte[] _payload;

        public FakeResponseWriter(string contentType, byte[]? payload = null)
        {
            ContentType = contentType;
            _payload = payload ?? [];
        }

        public string ContentType { get; }

        public async Task Write(Stream stream)
        {
            await stream.WriteAsync(_payload);
        }
    }
}
