using Backend.Models.Auth;
using Backend.Models.Nodes;
using Backend.Services.Users;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Schemas;

namespace Backend.Init;

/// <summary>
/// initializes database models
/// </summary>
public class DatabaseModelService : IHostedService {
    readonly IEntityManager database;

    /// <summary>
    /// creates a new <see cref="DatabaseModelService"/>
    /// </summary>
    /// <param name="database">access to database</param>
    public DatabaseModelService(IEntityManager database) {
        this.database = database;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken) {
        if (database.DBClient == null)
            return;
            
        ISchemaService schemaService = new SchemaService(database.DBClient);
        using Transaction transaction = database.Transaction();

        await schemaService.CreateOrUpdateSchema<ApiKey>(transaction);
        await schemaService.CreateOrUpdateSchema<User>(transaction);
        await schemaService.CreateOrUpdateSchema<Node>(transaction);
        await schemaService.CreateOrUpdateSchema<NodeLink>(transaction);
        await schemaService.CreateOrUpdateSchema<NodeType>(transaction);

        transaction.Commit();
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) {
        return Task.CompletedTask;
    }
}