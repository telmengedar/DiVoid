using System.Reflection;
using Pooshit.Ocelot.Clients;

namespace Backend.tests.Fixtures;

/// <summary>
/// Forwards every call to a real client; once armed, holds each UPDATE statement back until released.
/// </summary>
public class UpdateGateClient : DispatchProxy
{
    IDBClient? inner;
    volatile bool armed;
    readonly TaskCompletionSource updateStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// completes when the first UPDATE statement has reached the gate
    /// </summary>
    public Task UpdateStarted => updateStarted.Task;

    /// <summary>
    /// wraps a real client in a gate proxy
    /// </summary>
    /// <param name="real">client all calls are forwarded to</param>
    /// <param name="gate">the gate controlling the returned client</param>
    /// <returns>proxy client to hand to the entity manager</returns>
    public static IDBClient Wrap(IDBClient real, out UpdateGateClient gate)
    {
        IDBClient proxy = Create<IDBClient, UpdateGateClient>();
        gate = (UpdateGateClient)(object)proxy;
        gate.inner = real;
        return proxy;
    }

    /// <summary>
    /// starts holding back UPDATE statements
    /// </summary>
    public void Arm() => armed = true;

    /// <summary>
    /// lets all held UPDATE statements run
    /// </summary>
    public void Release() => release.TrySetResult();

    /// <inheritdoc/>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        bool isUpdate = armed
                        && targetMethod!.Name.StartsWith("NonQueryAsync")
                        && args!.OfType<string>().Any(s => s.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase));
        if (!isUpdate)
            return targetMethod!.Invoke(inner, args);

        return HeldUpdate(() => (Task<int>)targetMethod!.Invoke(inner, args)!);
    }

    async Task<int> HeldUpdate(Func<Task<int>> execute)
    {
        updateStarted.TrySetResult();
        await release.Task;
        return await execute();
    }
}
