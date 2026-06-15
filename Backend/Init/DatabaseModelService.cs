using Backend.Models.Auth;
using Backend.Models.Messages;
using Backend.Models.Nodes;
using Backend.Models.Organizations;
using Backend.Models.Users;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Schemas;
using Pooshit.Ocelot.Tokens;

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
        await schemaService.CreateOrUpdateSchema<Organization>(transaction);
        await schemaService.CreateOrUpdateSchema<UserOrganization>(transaction);
        await schemaService.CreateOrUpdateSchema<Node>(transaction);
        await schemaService.CreateOrUpdateSchema<NodeLink>(transaction);
        await schemaService.CreateOrUpdateSchema<NodeType>(transaction);
        await schemaService.CreateOrUpdateSchema<Message>(transaction);

        DateTime backfillNow = DateTime.UtcNow;
        await database.Update<Node>()
                      .Set(n => n.Created == backfillNow, n => n.LastUpdate == backfillNow)
                      .Where(n => n.Created == DateTime.MinValue)
                      .ExecuteAsync(transaction);

        await SeedBootstrapOrganizationIfMissing(transaction, backfillNow);

        transaction.Commit();
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) {
        return Task.CompletedTask;
    }

    /// <summary>
    /// seeds the bootstrap "DiVoid" organization at id 1 and backfills membership
    /// for existing users; idempotent — see <c>docs/architecture/organizations.md</c> §10.
    /// </summary>
    async Task SeedBootstrapOrganizationIfMissing(Transaction transaction, DateTime now) {
        long orgCount = await database.Load<Organization>(DB.Count()).ExecuteScalarAsync<long>(transaction);
        if (orgCount == 0) {
            await database.Insert<Organization>()
                          .Columns(o => o.Name, o => o.OwnerId, o => o.Created, o => o.LastUpdate)
                          .Values("DiVoid", 0L, now, now)
                          .ExecuteAsync(transaction);
        }

        long membershipCount = await database.Load<UserOrganization>(DB.Count()).ExecuteScalarAsync<long>(transaction);
        if (membershipCount == 0) {
            await foreach (User user in database.Load<User>(u => u.Id).ExecuteEntitiesAsync(transaction)) {
                await database.Insert<UserOrganization>()
                              .Columns(m => m.UserId, m => m.OrganizationId)
                              .Values(user.Id, Organization.BootstrapOrgIdConst)
                              .ExecuteAsync(transaction);
            }
        }
    }
}