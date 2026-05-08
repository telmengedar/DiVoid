using System;
using System.Threading.Tasks;
using Backend;
using Backend.Cli;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;

// Detect CLI mode: any arg that isn't a known host-config prefix triggers CLI dispatch.
// ASP.NET Core host builder consumes args like --environment, --urls, --contentRoot.
// Any other first arg is treated as a CLI verb.
bool isCli = args.Length > 0 && !args[0].StartsWith("--");

if (isCli) {
    await CliDispatcher.RunAsync(args);
} else {
    Host.CreateDefaultBuilder(args)
        .ConfigureWebHostDefaults(webBuilder => {
            webBuilder.UseStartup<Startup>()
                      .ConfigureKestrel((_, options) => {
                          options.ListenAnyIP(80, o => {
                              o.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
                          });
                      });
        }).Build().Run();
}

/// <summary>
/// Partial declaration exposes the compiler-generated Program class so that
/// <c>WebApplicationFactory&lt;Program&gt;</c> can reference it from the test project.
/// </summary>
public partial class Program { }
