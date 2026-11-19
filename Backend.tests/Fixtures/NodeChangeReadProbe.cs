using System.Reflection;
using Pooshit.Ocelot.Clients;

namespace Backend.tests.Fixtures;

/// <summary>Forwards SQL to a real provider while counting and optionally pausing reads.</summary>
public class NodeChangeReadProbe : DispatchProxy {
    IDBClient inner = null!;
    int reads;

    /// <summary>The optional real-read barrier.</summary>
    public Func<string, Task>? BeforeRead { get; set; }

    /// <summary>The optional transaction preparation observation.</summary>
    public Action? BeforeTransaction { get; set; }

    /// <summary>The observed SQL read count.</summary>
    public int Reads => Volatile.Read(ref reads);

    /// <summary>Wraps the actual provider without changing its dialect or transaction semantics.</summary>
    /// <param name="client">The actual provider.</param>
    /// <param name="beforeRead">The optional read barrier.</param>
    /// <returns>The proxy and selected probe.</returns>
    public static (IDBClient Client, NodeChangeReadProbe Probe) Wrap(IDBClient client, Func<string, Task>? beforeRead = null) {
        IDBClient proxy = Create<IDBClient, NodeChangeReadProbe>();
        NodeChangeReadProbe probe = (NodeChangeReadProbe)(object)proxy;
        probe.inner = client;
        probe.BeforeRead = beforeRead;
        return (proxy, probe);
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
        if (targetMethod!.Name == "Transaction")
            BeforeTransaction?.Invoke();
        string? sql = args?.OfType<string>().FirstOrDefault();
        if (sql?.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) == true) {
            Interlocked.Increment(ref reads);
            if (BeforeRead != null)
                BeforeRead(sql).GetAwaiter().GetResult();
        }
        try {
            return targetMethod!.Invoke(inner, args);
        } catch (TargetInvocationException exception) when (exception.InnerException != null) {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}
