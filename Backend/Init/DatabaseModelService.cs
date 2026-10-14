using Backend.Models.Auth;
using Backend.Models.Messages;
using Backend.Models.Nodes;
using Backend.Models.Users;
using Microsoft.Extensions.Logging;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Schemas;

namespace Backend.Init;

/// <summary>
/// initializes database models
/// </summary>
public class DatabaseModelService : IHostedService {
    readonly IEntityManager database;
    readonly ILogger<DatabaseModelService> logger;

    /// <summary>
    /// creates a new <see cref="DatabaseModelService"/>
    /// </summary>
    /// <param name="database">access to database</param>
    /// <param name="logger">access to logging</param>
    public DatabaseModelService(IEntityManager database, ILogger<DatabaseModelService> logger) {
        this.database = database;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken) {
        if (database.DBClient == null)
            return;

        await new PgvectorBootstrap(database.DBClient, logger).EnsureAsync();

        ISchemaService schemaService = new SchemaService(database.DBClient);
        using Transaction transaction = database.Transaction();

        await schemaService.CreateOrUpdateSchema<ApiKey>(transaction);
        await schemaService.CreateOrUpdateSchema<User>(transaction);
        await schemaService.CreateOrUpdateSchema<Node>(transaction);
        await schemaService.CreateOrUpdateSchema<NodeLink>(transaction);
        await schemaService.CreateOrUpdateSchema<NodeType>(transaction);
        await schemaService.CreateOrUpdateSchema<Message>(transaction);

        DateTime backfillNow = DateTime.UtcNow;
        await database.Update<Node>()
                      .Set(n => n.Created == backfillNow, n => n.LastUpdate == backfillNow)
                      .Where(n => n.Created == DateTime.MinValue)
                      .ExecuteAsync(transaction);

        transaction.Commit();
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) {
        return Task.CompletedTask;
    }
}