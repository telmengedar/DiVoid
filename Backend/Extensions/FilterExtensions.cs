using Pooshit.AspNetCore.Services.Data;
using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Fields;
using Pooshit.Ocelot.Tokens;

namespace Backend.Extensions;

/// <summary>
/// extensions for filter data
/// </summary>
public static class FilterExtensions
{

    /// <summary>
    /// applies a standard list filter to a load operation
    /// </summary>
    /// <typeparam name="T">type of entities to load</typeparam>
    /// <param name="operation">operation to modify</param>
    /// <param name="filter">filter to apply</param>
    /// <param name="ignoreLimits">determines whether to ignore limit fields in filter - usually used in internal listings</param>
    /// <returns>load operation for fluent behavior</returns>
    public static LoadOperation<T> ApplyFilter<T>(this LoadOperation<T> operation, PageFilter filter, bool ignoreLimits = false)
    {
        if (!ignoreLimits)
        {
            if (filter.Count is null or > 500)
                filter.Count = 500;
        }

        if (filter.Count <= 0)
            throw new ArgumentException($"A count of '{filter.Count}' makes no sense", nameof(filter.Count));

        if (filter.Count.HasValue)
            operation.Limit(filter.Count.Value);
        if (filter.Continue.HasValue)
            operation.Offset(filter.Continue.Value);
        return operation;
    }

    /// <summary>
    /// applies a standard list filter to a load operation
    /// </summary>
    /// <typeparam name="T">type of entities to load</typeparam>
    /// <param name="operation">operation to modify</param>
    /// <param name="filter">filter to apply</param>
    /// <param name="ignoreLimits">determines whether to ignore limit fields in filter</param>
    /// <returns>load operation for fluent behavior</returns>
    public static LoadOperation<T> ApplyFilter<T>(this LoadOperation<T> operation, ListFilter filter, bool ignoreLimits = false)
    {
        ApplyFilter(operation, (PageFilter)filter, ignoreLimits);
        if (string.IsNullOrEmpty(filter.Sort)) return operation;

        string[] split = filter.Sort.Split('.');
        ISqlToken field = split.Length == 1 ? DB.Column(split[0]) : DB.Column(split[0], split[1]);
        operation.OrderBy(new OrderByCriteria(field, !filter.Descending));

        return operation;
    }

    /// <summary>
    /// applies a standard list filter to a load operation
    /// </summary>
    /// <typeparam name="T">type of entities to load</typeparam>
    /// <typeparam name="TEntity">type of mapped entity</typeparam>
    /// <param name="operation">operation to modify</param>
    /// <param name="filter">filter to apply</param>
    /// <param name="ignoreLimits">determines whether to ignore limit fields in filter</param>
    /// <returns>load operation for fluent behavior</returns>
    public static LoadOperation<T> ApplyFilter<T, TEntity>(this LoadOperation<T> operation, ListFilter filter, FieldMapper<TEntity> mapper, bool ignoreLimits = false)
    {
        ApplyFilter(operation, (PageFilter)filter, ignoreLimits);
        if (string.IsNullOrEmpty(filter.Sort)) return operation;

        operation.OrderBy(new OrderByCriteria(mapper[filter.Sort].Field, !filter.Descending));

        return operation;
    }

    /// <summary>
    /// determines whether a string contains wildcards
    /// </summary>
    /// <param name="data">data to analyze</param>
    /// <returns>true if data contains wildcards, false otherwise</returns>
    public static bool ContainsWildcards(this string data)
    {
        return data.Contains('%') || data.Contains('_');
    }
}
