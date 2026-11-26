using Backend.Extensions;
using Backend.Models.Nodes;
using Backend.Models.Organizations;
using Backend.Models.Users;
using Pooshit.AspNetCore.Services.Convert;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Errors.Exceptions;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Tokens;

namespace Backend.Services.Organizations;

/// <summary>Manages organizations and assignment references.</summary>
public class OrganizationService(IEntityManager database) : IOrganizationService {
    /// <inheritdoc />
    public async Task<Organization> Create(Organization organization) {
        using Transaction transaction = database.Transaction();
        await ValidateHomeNode(database, organization.HomeNodeId, transaction);
        long id = await database.Insert<Organization>().Columns(o => o.Name, o => o.HomeNodeId)
            .Values(organization.Name, organization.HomeNodeId).ReturnID().ExecuteAsync(transaction);
        transaction.Commit();
        return await Get(id);
    }

    /// <inheritdoc />
    public async Task<Organization> Get(long id) {
        Organization organization = await new OrganizationMapper().EntityFromOperation(database.Load<Organization>().Where(o => o.Id == id));
        return organization ?? throw new NotFoundException<Organization>(id);
    }

    /// <inheritdoc />
    public AsyncPageResponseWriter<Organization> List(ListFilter filter) {
        filter ??= new();
        OrganizationMapper mapper = new();
        LoadOperation<Organization> operation = mapper.CreateOperation(database, filter.Fields);
        operation.ApplyFilter(filter, mapper);
        return new(mapper.EntitiesFromOperation(operation),
            () => database.Load<Organization>(DB.Count()).ExecuteScalarAsync<long>(), filter.Continue);
    }

    /// <inheritdoc />
    public async Task<Organization> Patch(long id, PatchOperation[] patches) {
        using Transaction transaction = database.Transaction();
        await ValidatePatchReference(database, patches, "/homeNodeId", transaction, homeNode: true);
        if (await database.Update<Organization>().Patch(patches).Where(o => o.Id == id).ExecuteAsync(transaction) == 0)
            throw new NotFoundException<Organization>(id);
        transaction.Commit();
        return await Get(id);
    }

    /// <inheritdoc />
    public async Task Delete(long id) {
        using Transaction transaction = database.Transaction();
        await ValidateReference(database, id, transaction);
        if (await database.Load<User>(DB.Count()).Where(u => u.OrganizationId == id).ExecuteScalarAsync<long>(transaction) != 0 ||
            await database.Load<Node>(DB.Count()).Where(n => n.OrganizationId == id).ExecuteScalarAsync<long>(transaction) != 0)
            throw new ArgumentException("The organization is still referenced by users or nodes.");
        if (await database.Delete<Organization>().Where(o => o.Id == id).ExecuteAsync(transaction) == 0)
            throw new NotFoundException<Organization>(id);
        transaction.Commit();
    }

    internal static async Task ValidateReference(IEntityManager database, long? id, Transaction transaction) {
        if (!id.HasValue)
            return;
        long organizationId = id.Value;
        if (await database.Update<Organization>().Set(o => o.Id == o.Id).Where(o => o.Id == organizationId).ExecuteAsync(transaction) == 0)
            throw new NotFoundException<Organization>(organizationId);
    }

    internal static async Task ValidateHomeNode(IEntityManager database, long? id, Transaction transaction) {
        if (!id.HasValue)
            return;
        long nodeId = id.Value;
        if (await database.Load<Node>(DB.Count()).Where(n => n.Id == nodeId).ExecuteScalarAsync<long>(transaction) == 0)
            throw new NotFoundException<Node>(nodeId);
    }

    internal static async Task ValidatePatchReference(IEntityManager database, PatchOperation[] patches, string path, Transaction transaction, bool homeNode = false) {
        foreach (PatchOperation patch in patches.Where(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase))) {
            if (patch.Op != Pooshit.AspNetCore.Services.Patches.Patch.Op_Replace)
                throw new ArgumentException("Reference fields support replace only.");
            long? id = (long?)Converter.Convert(patch.Value, typeof(long?), true);
            if (homeNode)
                await ValidateHomeNode(database, id, transaction);
            else
                await ValidateReference(database, id, transaction);
        }
    }
}
