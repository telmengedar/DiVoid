using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Pooshit.Http;

namespace Backend.tests;

/// <summary>
/// Shared setup helpers for HTTP integration tests.
/// </summary>
public static class TestSetup
{
    /// <summary>
    /// base URL that matches the <see cref="WebApplicationFactory{TEntryPoint}"/> test server address
    /// </summary>
    public const string BaseUrl = "http://localhost";

    /// <summary>
    /// creates a <see cref="WebApplicationFactory{TEntryPoint}"/> configured for integration testing:
    /// auth disabled, in-memory SQLite database, no external dependencies
    /// </summary>
    /// <param name="dbSuffix">optional suffix to distinguish database instances across fixtures</param>
    /// <returns>configured factory ready for <see cref="HttpServiceFor"/></returns>
    public static WebApplicationFactory<Program> CreateTestFactory(string? dbSuffix = null)
    {
        string dbName = $"/tmp/divoid_http_test_{dbSuffix ?? Guid.NewGuid().ToString("N")}.db3";
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder => {
            builder.ConfigureAppConfiguration((_, config) => {
                config.AddInMemoryCollection(new Dictionary<string, string?> {
                    ["Auth:Enabled"] = "false",
                    ["Database:Type"] = "Sqlite",
                    ["Database:Source"] = dbName
                });
            });
        });
    }

    /// <summary>
    /// wraps the test server's <see cref="System.Net.Http.HttpMessageHandler"/> in an
    /// <see cref="IHttpService"/> so tests use the Pooshit.Http contract rather than raw
    /// <see cref="System.Net.Http.HttpClient"/>
    /// </summary>
    /// <param name="factory">the configured test factory</param>
    /// <returns><see cref="IHttpService"/> bound to the loopback test server</returns>
    public static IHttpService HttpServiceFor(WebApplicationFactory<Program> factory)
        => new HttpService(factory.Server.CreateHandler());
}
