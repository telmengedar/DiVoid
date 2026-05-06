using Microsoft.AspNetCore.Mvc.Formatters;
using Pooshit.AspNetCore.Services.Formatters.DataStream;

namespace Backend.Formatters; 

/// <summary>
/// output formatter for streamed json
/// </summary>
public class JsonStreamOutputFormatter : OutputFormatter
{

    /// <summary>
    /// creates a new <see cref="JsonStreamOutputFormatter"/>
    /// </summary>
    public JsonStreamOutputFormatter() {
        SupportedMediaTypes.Add("application/json");
        SupportedMediaTypes.Add("application/xml");
    }

    /// <inheritdoc />
    protected override bool CanWriteType(Type type) {
        return typeof(IResponseWriter).IsAssignableFrom(type);
    }

    /// <inheritdoc />
    public override Task WriteResponseBodyAsync(OutputFormatterWriteContext context) {
        IResponseWriter response = (IResponseWriter)context.Object;
        context.HttpContext.Response.ContentType = response.ContentType;
        return response.Write(context.HttpContext.Response.Body);
    }
}