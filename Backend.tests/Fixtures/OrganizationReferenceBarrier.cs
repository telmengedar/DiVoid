using System.Reflection;
using Pooshit.Ocelot.Clients;

namespace Backend.tests.Fixtures;

/// <summary>Pauses a real organization parent lock while another producer competes.</summary>
public class OrganizationReferenceBarrier : DispatchProxy {
    IDBClient inner = null!;
    int intercepted;
    readonly TaskCompletionSource locked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The observed real parent lock.</summary>
    public Task Locked => locked.Task;

    /// <summary>Wraps a real database client.</summary>
    /// <param name="client">The real provider.</param>
    /// <param name="barrier">The lock barrier.</param>
    /// <returns>The wrapped provider.</returns>
    public static IDBClient Wrap(IDBClient client, out OrganizationReferenceBarrier barrier) {
        IDBClient proxy = Create<IDBClient, OrganizationReferenceBarrier>();
        barrier = (OrganizationReferenceBarrier)(object)proxy;
        barrier.inner = client;
        return proxy;
    }

    /// <summary>Releases the producer holding the parent lock.</summary>
    public void Release() => release.TrySetResult();

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? method, object?[]? arguments) {
        bool parent = method!.Name == "NonQueryAsync" && arguments!.OfType<string>().Any(sql => sql.TrimStart().StartsWith("UPDATE organization", StringComparison.OrdinalIgnoreCase));
        if (parent && Interlocked.CompareExchange(ref intercepted, 1, 0) == 0)
            return Pause((Task<int>)method.Invoke(inner, arguments)!);
        return method.Invoke(inner, arguments);
    }

    async Task<int> Pause(Task<int> operation) {
        int affected = await operation;
        locked.TrySetResult();
        await release.Task;
        return affected;
    }
}
