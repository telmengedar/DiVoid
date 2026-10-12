using System.Reflection;
using System.Text.RegularExpressions;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Info;

namespace Backend.tests.Fixtures;

/// <summary>
/// Forwards every call to a real SQLite client while presenting the Postgres dialect to the entity manager, and executes each embedding UPDATE as a marker write on the same rows,
/// so the WHERE clause is evaluated by the database while the Postgres-only <c>embedding()</c> call never runs.
/// </summary>
public class EmbeddingWriteMarkerClient : DispatchProxy
{
    static readonly Regex EmbeddingSet = new(@"^(?<head>\s*UPDATE node SET\s+)""?embedding""?\s*=.*?(?<tail>\s+WHERE\b.*)$", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex AnyArray = new(@"= ANY\s*\(\s*@(?<index>\d+)\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    readonly IDBInfo postgres = new PostgreInfo();
    IDBClient? inner;
    Func<Task>? beforeWrite;

    /// <summary>
    /// wraps a real client in a marker proxy
    /// </summary>
    /// <param name="real">client all calls are forwarded to</param>
    /// <param name="beforeWrite">awaited before each embedding UPDATE is executed, null for none</param>
    /// <returns>proxy client to hand to the entity manager</returns>
    public static IDBClient Wrap(IDBClient real, Func<Task>? beforeWrite = null)
    {
        IDBClient proxy = Create<IDBClient, EmbeddingWriteMarkerClient>();
        EmbeddingWriteMarkerClient marker = (EmbeddingWriteMarkerClient)(object)proxy;
        marker.inner = real;
        marker.beforeWrite = beforeWrite;
        return proxy;
    }

    /// <inheritdoc/>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == "get_DBInfo")
            return postgres;

        args = ExpandArrayParameters(args);
        string? sql = targetMethod.Name.StartsWith("NonQueryAsync") ? args!.OfType<string>().FirstOrDefault() : null;
        Match match = sql == null ? Match.Empty : EmbeddingSet.Match(sql);
        if (!match.Success)
            return targetMethod.Invoke(inner, args);

        string markerSql = match.Groups["head"].Value + "\"substance\" = 'embedded'" + match.Groups["tail"].Value;
        object?[] forwarded = args!.Select(arg => arg == sql ? markerSql : arg).ToArray();
        return ExecuteMarker(() => (Task<int>)targetMethod.Invoke(inner, forwarded)!);
    }

    static object?[]? ExpandArrayParameters(object?[]? args)
    {
        string? sql = args?.OfType<string>().FirstOrDefault();
        object[]? parameters = args?.Select(arg => arg as object[] ?? (arg as IEnumerable<object>)?.ToArray()).FirstOrDefault(arg => arg != null);
        if (sql == null || parameters == null || !AnyArray.IsMatch(sql))
            return args;

        string expanded = AnyArray.Replace(sql, m => "IN (" + string.Join(", ", ((IEnumerable<long>)parameters[int.Parse(m.Groups["index"].Value) - 1]).Select(id => id.ToString())) + ")");
        return args!.Select(arg => arg == sql ? expanded : arg).ToArray();
    }

    async Task<int> ExecuteMarker(Func<Task<int>> execute)
    {
        if (beforeWrite != null)
            await beforeWrite();
        return await execute();
    }
}
