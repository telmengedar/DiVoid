using System.Reflection;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Info;

namespace Backend.tests.Fixtures;

/// <summary>
/// Client which answers scalar queries and non-queries from scripts and records every statement it receives.
/// </summary>
public class ScriptedDbClient : DispatchProxy {
    readonly List<string> statements = [];
    IDBInfo? info;
    Func<string, object?>? scalar;
    Func<string, int>? nonQuery;

    /// <summary>
    /// statements received so far, in order
    /// </summary>
    public string[] Statements {
        get {
            lock (statements)
                return statements.ToArray();
        }
    }


    /// <summary>
    /// creates a scripted client
    /// </summary>
    /// <param name="info">dialect the client reports</param>
    /// <param name="scalar">answers each scalar query by its statement</param>
    /// <param name="nonQuery">answers each non-query by its statement, throwing to fail it</param>
    /// <param name="script">the script recording the statements of the returned client</param>
    /// <returns>client to hand to the code under test</returns>
    public static IDBClient Create(IDBInfo info, Func<string, object?> scalar, Func<string, int> nonQuery, out ScriptedDbClient script) {
        IDBClient proxy = Create<IDBClient, ScriptedDbClient>();
        script = (ScriptedDbClient)(object)proxy;
        script.info = info;
        script.scalar = scalar;
        script.nonQuery = nonQuery;
        return proxy;
    }


    /// <inheritdoc/>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
        if (targetMethod!.Name == "get_DBInfo")
            return info;

        string statement = args!.OfType<string>().First();
        lock (statements)
            statements.Add(statement);

        if (targetMethod.Name.StartsWith("ScalarAsync"))
            return Task.FromResult(scalar!(statement));

        if (targetMethod.Name.StartsWith("NonQueryAsync"))
            return Run(() => nonQuery!(statement));

        throw new NotSupportedException(targetMethod.Name);
    }


    static Task<int> Run(Func<int> execute) {
        try {
            return Task.FromResult(execute());
        } catch (Exception ex) {
            return Task.FromException<int>(ex);
        }
    }
}
