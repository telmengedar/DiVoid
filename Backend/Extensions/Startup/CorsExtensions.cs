namespace Backend.Extensions.Startup;

/// <summary>
/// extensions for registering and activating the DiVoid CORS policy
/// </summary>
public static class CorsExtensions {

    /// <summary>
    /// name of the CORS policy registered by <see cref="AddDivoidCors"/>
    /// </summary>
    public const string PolicyName = "DivoidFrontend";

    /// <summary>
    /// registers the DiVoid CORS policy from the <c>Cors:AllowedOrigins</c> configuration key.
    /// when the list is empty or absent, no origins are allowed (CORS effectively off).
    /// </summary>
    /// <param name="services">service collection to add CORS services to</param>
    /// <param name="configuration">app configuration</param>
    /// <returns>service collection for fluent chaining</returns>
    public static IServiceCollection AddDivoidCors(this IServiceCollection services, IConfiguration configuration) {
        string[] allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

        services.AddCors(options => {
            options.AddPolicy(PolicyName, policy => {
                if (allowedOrigins.Length == 0) {
                    // No origins configured — CORS policy is a no-op (nothing is allowed).
                    policy.SetIsOriginAllowed(_ => false);
                    return;
                }

                policy.WithOrigins(allowedOrigins)
                      .WithMethods("GET", "POST", "PATCH", "DELETE", "OPTIONS")
                      .WithHeaders("Authorization", "Content-Type", "Accept")
                      .DisallowCredentials();
            });
        });

        return services;
    }

    /// <summary>
    /// activates the <see cref="PolicyName"/> CORS middleware.
    /// must be called after <c>UseRouting</c> and before <c>UseAuthentication</c>.
    /// </summary>
    /// <param name="app">app builder</param>
    /// <returns>app builder for fluent chaining</returns>
    public static IApplicationBuilder UseDivoidCors(this IApplicationBuilder app) {
        return app.UseCors(PolicyName);
    }
}
