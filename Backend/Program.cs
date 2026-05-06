using Backend;
using Microsoft.AspNetCore.Server.Kestrel.Core;

Host.CreateDefaultBuilder(args)
            .ConfigureWebHostDefaults(webBuilder => {
                webBuilder.UseStartup<Startup>()
                          .ConfigureKestrel((_, options) => {
                              options.ListenAnyIP(80, o => {
                                  o.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;

                                  //o.UseHttps();
                              });
                          });
            }).Build().Run();