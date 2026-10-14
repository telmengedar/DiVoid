using Microsoft.Extensions.Logging;
using Npgsql;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Info;

namespace Backend.Init;

/// <summary>
/// enables the pgvector extension on Postgres and refuses to continue when it is unavailable
/// </summary>
public class PgvectorBootstrap {
    static readonly Version MinimumVersion = new(0, 7, 0);

    readonly IDBClient client;
    readonly ILogger logger;

    /// <summary>
    /// creates a new <see cref="PgvectorBootstrap"/>
    /// </summary>
    /// <param name="client">client of the database to prepare</param>
    /// <param name="logger">access to logging</param>
    public PgvectorBootstrap(IDBClient client, ILogger logger) {
        this.client = client;
        this.logger = logger;
    }


    /// <summary>
    /// makes sure the pgvector extension of at least the minimum version is enabled in the database
    /// </summary>
    /// <exception cref="InvalidOperationException">extension can not be enabled or is too old</exception>
    public async Task EnsureAsync() {
        if (client.DBInfo is not PostgreInfo)
            return;

        string version = await InstalledVersion();
        if (version != null) {
            logger.LogDebug("event=db.pgvector action=present version={Version}", version);
            RequireMinimumVersion(version);
            return;
        }

        try {
            await client.NonQueryAsync("CREATE EXTENSION IF NOT EXISTS vector");
        } catch (Exception ex) {
            string database = (await client.ScalarAsync("SELECT current_database()"))?.ToString();
            logger.LogError(ex, "event=db.pgvector action=failed database={Database}", database);
            throw new InvalidOperationException(
                $"pgvector is required but the 'vector' extension is not enabled in database '{database}' and DiVoid could not enable it ({Describe(ex)}). " +
                "Someone with the privilege must run 'CREATE EXTENSION vector;' in that database (installing pgvector on the Postgres server first if it is missing), then DiVoid must be restarted.",
                ex);
        }

        version = await InstalledVersion();
        logger.LogInformation("event=db.pgvector action=created version={Version}", version);
        RequireMinimumVersion(version);
    }


    static string Describe(Exception exception) {
        while (exception.InnerException != null)
            exception = exception.InnerException;

        return exception is PostgresException postgres ? $"SQLSTATE {postgres.SqlState}: {postgres.MessageText}" : exception.Message;
    }


    async Task<string> InstalledVersion() {
        return (await client.ScalarAsync("SELECT extversion FROM pg_extension WHERE extname = 'vector'"))?.ToString();
    }


    void RequireMinimumVersion(string version) {
        if (Version.Parse(version) >= MinimumVersion)
            return;

        logger.LogError("event=db.pgvector action=failed version={Version}", version);
        throw new InvalidOperationException($"pgvector {MinimumVersion} or later is required but version {version} is installed. Upgrade the extension with 'ALTER EXTENSION vector UPDATE;' and restart DiVoid.");
    }
}
