using System.Threading;
using Backend.Extensions;
using Backend.Models.Organizations;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Errors.Exceptions;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Expressions;
using Pooshit.Ocelot.Fields;
using Pooshit.Ocelot.Tokens;

namespace Backend.Services.Organizations;

/// <inheritdoc />
public class OrganizationService(IEntityManager database) : IOrganizationService
{
    readonly IEntityManager database = database;

    /// <inheritdoc />
    public async Task<OrganizationDetails> CreateOrganization(OrganizationDetails organization, long callerId)
    {
        DateTime now = DateTime.UtcNow;
        long id = await database.Insert<Organization>()
                                .Columns(o => o.Name, o => o.OwnerId, o => o.Created, o => o.LastUpdate)
                                .Values(organization.Name, callerId, now, now)
                                .ReturnID()
                                .ExecuteAsync();
        return await GetOrganizationById(id, accessibleOrgs: null, isAdmin: true);
    }

    /// <inheritdoc />
    public async Task<OrganizationDetails> GetOrganizationById(long id, long[] accessibleOrgs, bool isAdmin)
    {
        if (!OrgAccessible(id, accessibleOrgs, isAdmin))
            throw new NotFoundException<Organization>(id);

        OrganizationMapper mapper = new();
        OrganizationDetails row = await mapper.EntityFromOperation(
            mapper.CreateOperation(database).Where(o => o.Id == id));
        if (row == null)
            throw new NotFoundException<Organization>(id);

        List<long> members = [];
        await foreach (UserOrganization m in database.Load<UserOrganization>(u => u.UserId)
                                                     .Where(u => u.OrganizationId == id)
                                                     .ExecuteEntitiesAsync())
        {
            members.Add(m.UserId);
        }
        row.Members = [.. members];
        return row;
    }

    /// <inheritdoc />
    public Task<AsyncPageResponseWriter<OrganizationDetails>> ListPaged(OrganizationFilter filter, long[] accessibleOrgs, bool isAdmin, CancellationToken ct = default)
    {
        filter ??= new();
        OrganizationMapper mapper = new();
        LoadOperation<Organization> operation = mapper.CreateOperation(database, filter.Fields);
        operation.ApplyFilter(filter, mapper);

        PredicateExpression<Organization> predicate = BuildListPredicate(filter, accessibleOrgs, isAdmin);
        if (predicate != null)
            operation.Where(predicate.Content);

        LoadOperation<Organization> countOp = mapper.CreateOperation(database, DB.Count());
        if (predicate != null)
            countOp.Where(predicate.Content);

        return Task.FromResult(new AsyncPageResponseWriter<OrganizationDetails>(
            mapper.EntitiesFromOperation(operation, filter.Fields),
            () => countOp.ExecuteScalarAsync<long>(),
            filter.Continue
        ));
    }

    /// <inheritdoc />
    public async Task<long[]> GetUserOrganizationIds(long userId)
    {
        List<long> ids = [];
        await foreach (UserOrganization m in database.Load<UserOrganization>(u => u.OrganizationId)
                                                     .Where(u => u.UserId == userId)
                                                     .ExecuteEntitiesAsync())
        {
            ids.Add(m.OrganizationId);
        }
        if (ids.Count == 0)
            ids.Add(Organization.BootstrapOrgIdConst);
        return [.. ids];
    }

    /// <inheritdoc />
    public async Task AddMember(long orgId, long userId)
    {
        long exists = await database.Load<UserOrganization>(DB.Count())
                                     .Where(m => m.UserId == userId && m.OrganizationId == orgId)
                                     .ExecuteScalarAsync<long>();
        if (exists > 0) return;

        await database.Insert<UserOrganization>()
                      .Columns(m => m.UserId, m => m.OrganizationId)
                      .Values(userId, orgId)
                      .ExecuteAsync();
    }

    static PredicateExpression<Organization> BuildListPredicate(OrganizationFilter filter, long[] accessibleOrgs, bool isAdmin)
    {
        PredicateExpression<Organization> predicate = null;

        if (!isAdmin && accessibleOrgs != null)
        {
            predicate &= accessibleOrgs.Length == 0
                ? new PredicateExpression<Organization>(o => false)
                : new PredicateExpression<Organization>(o => o.Id.In(accessibleOrgs));
        }

        if (filter.Id?.Length > 0)
            predicate &= new PredicateExpression<Organization>(o => o.Id.In(filter.Id));

        if (filter.Name?.Length > 0)
        {
            if (filter.Name.Any(n => n.ContainsWildcards()))
            {
                PredicateExpression<Organization> namePred = null;
                foreach (string n in filter.Name)
                    namePred |= o => o.Name.Like(n);
                predicate &= namePred;
            }
            else
            {
                predicate &= new PredicateExpression<Organization>(o => o.Name.In(filter.Name));
            }
        }

        return predicate;
    }

    static bool OrgAccessible(long orgId, long[] accessibleOrgs, bool isAdmin)
    {
        if (isAdmin || accessibleOrgs == null) return true;
        foreach (long o in accessibleOrgs)
            if (o == orgId) return true;
        return false;
    }
}
