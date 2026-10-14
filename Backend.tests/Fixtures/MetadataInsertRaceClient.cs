using System.Reflection;
using Pooshit.Ocelot.Clients;

namespace Backend.tests.Fixtures;

/// <summary>
/// Forwards every call to a real client; before the first INSERT into the metadata table it runs a callback, so a competing writer wins the primary key.
/// </summary>
public class MetadataInsertRaceClient : DispatchProxy
{
    IDBClient? inner;
    Func<Task>? competitor;

    /// <summary>
    /// wraps a real client in a race proxy
    /// </summary>
    /// <param name="real">client all calls are forwarded to</param>
    /// <param name="beforeFirstInsert">awaited once, immediately before the first metadata INSERT is executed</param>
    /// <returns>proxy client to hand to the entity manager</returns>
    public static IDBClient Wrap(IDBClient real, Func<Task> beforeFirstInsert)
    {
        IDBClient proxy = Create<IDBClient, MetadataInsertRaceClient>();
        MetadataInsertRaceClient race = (MetadataInsertRaceClient)(object)proxy;
        race.inner = real;
        race.competitor = beforeFirstInsert;
        return proxy;
    }

    /// <inheritdoc/>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        bool isMetadataInsert = targetMethod!.Name.StartsWith("NonQueryAsync")
                                && args!.OfType<string>().Any(s => s.Contains("INSERT INTO", StringComparison.OrdinalIgnoreCase) && s.Contains("metadata", StringComparison.OrdinalIgnoreCase));
        Func<Task>? pending = isMetadataInsert ? Interlocked.Exchange(ref competitor, null) : null;
        if (pending == null)
            return targetMethod.Invoke(inner, args);

        return CompeteThenInsert(pending, () => (Task<int>)targetMethod.Invoke(inner, args)!);
    }

    static async Task<int> CompeteThenInsert(Func<Task> pending, Func<Task<int>> insert)
    {
        await pending();
        return await insert();
    }
}
