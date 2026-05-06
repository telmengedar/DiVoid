using Microsoft.Data.Sqlite;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Info;
using Npgsql;

namespace Backend.Extensions.Startup;

/// <summary>
/// extensions used to initialize database connection
/// </summary>
public static class DatabaseExtensions {

    /// <summary>
    /// adds database service to service collection
    /// </summary>
    /// <param name="services">service collection to add service to</param>
    /// <param name="configuration">app configuration containing database connection info</param>
    /// <returns>service collection for fluent behavior</returns>
    public static IServiceCollection ConfigureDatabaseService(this IServiceCollection services, IConfiguration configuration) {
        switch (configuration["Database:Type"]) {
            case "Sqlite":
                return services.AddSingleton<IEntityManager>(s => new EntityManager(ClientFactory.Create(() => new SqliteConnection($"Data Source={configuration["Database:Source"]}"), new SQLiteInfo(), false, true)));
            default:
                int commandTimeout = configuration.GetValue("Database:CommandTimeout", 300);
                string connectionstring = $"Server={configuration["Database:Host"]};Port={configuration["Database:Port"]};Database={configuration["Database:Instance"]};User Id={configuration["Database:User"]};Password={configuration["Database:Password"]};Command Timeout={commandTimeout};";
                return services.AddSingleton<IEntityManager>(s => new EntityManager(ClientFactory.Create(() => new NpgsqlConnection(connectionstring), new PostgreInfo(), true)));
        }
    }
}