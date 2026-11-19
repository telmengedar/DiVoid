using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Backend.tests.Fixtures;

internal sealed class InboxAbortResponseFilter : IStartupFilter {
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
        app.Use(async (context, invoke) => {
            await invoke();
            if (context.Request.Method == "POST" && context.Request.Path == "/api/messages")
                context.Abort();
        });
        next(app);
    };
}
