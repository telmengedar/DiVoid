using System.Collections;
using System.Data;
using System.Reflection;
using Pooshit.Ocelot.Clients;

namespace Backend.tests.Fixtures;

/// <summary>
/// A <see cref="DispatchProxy"/>-based spy for <see cref="IDBClient"/> that forwards all calls
/// to the wrapped real client, but rejects any call whose <c>IEnumerable&lt;object&gt;</c>
/// parameters argument contains a <see cref="CancellationToken"/> value.
///
/// This simulates the Npgsql behaviour seen in bug #305:
///   "Writing values of System.Threading.CancellationToken is not supported for parameters
///    having no NpgsqlDbType or DataTypeName"
///
/// SQLite silently accepts the CT value (stringifying it), which is why the bug only surfaced
/// in production on Postgres. This spy surfaces the same error class on SQLite so the
/// regression test runs in the normal NUnit test suite.
/// </summary>
public class CancellationTokenRejectingDbClientSpy : DispatchProxy
{
    IDBClient? inner;

    /// <summary>
    /// Wraps <paramref name="real"/> in a spy proxy and returns it as <see cref="IDBClient"/>.
    /// </summary>
    public static IDBClient Wrap(IDBClient real)
    {
        IDBClient proxy = Create<IDBClient, CancellationTokenRejectingDbClientSpy>();
        CancellationTokenRejectingDbClientSpy spy = (CancellationTokenRejectingDbClientSpy)(object)proxy;
        spy.inner = real;
        return proxy;
    }

    /// <inheritdoc/>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null)
            return null;

        // Inspect every IEnumerable<object> or object[] argument for CancellationToken values.
        if (args != null) {
            foreach (object? arg in args) {
                if (arg is IEnumerable enumerable and not string) {
                    foreach (object? item in enumerable) {
                        if (item is CancellationToken) {
                            throw new InvalidOperationException(
                                "CancellationToken SQL parameter detected: " +
                                "System.Threading.CancellationToken is not a bindable SQL value. " +
                                "This simulates the Npgsql error from bug #305. " +
                                "Fix: call ExecuteEntitiesAsync() without the CancellationToken argument.");
                        }
                    }
                }
                if (arg is object[] objArray) {
                    foreach (object? item in objArray) {
                        if (item is CancellationToken) {
                            throw new InvalidOperationException(
                                "CancellationToken SQL parameter detected (object[]): " +
                                "System.Threading.CancellationToken is not a bindable SQL value. " +
                                "This simulates the Npgsql error from bug #305. " +
                                "Fix: call ExecuteEntitiesAsync() without the CancellationToken argument.");
                        }
                    }
                }
            }
        }

        return targetMethod.Invoke(inner, args);
    }
}
