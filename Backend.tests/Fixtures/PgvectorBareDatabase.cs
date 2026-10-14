using Backend.Init;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Info;

namespace Backend.tests.Fixtures;

/// <summary>
/// throwaway database on the server named by POSTGRES_CONNECTION without the vector extension, plus a login role which is neither owner nor superuser
/// </summary>
public sealed class PgvectorBareDatabase : IDisposable {
    const string RolePassword = "role-pw-1";

    readonly string adminConnection;
    readonly string name = "divoid_pgv_" + Guid.NewGuid().ToString("N")[..12];
    readonly string role;
    readonly string adminDatabaseConnection;
    readonly string roleConnection;

    /// <summary>
    /// creates the database without the extension, and the role which may connect to it
    /// </summary>
    public PgvectorBareDatabase() {
        adminConnection = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")!;
        role = name + "_role";
        Execute(adminConnection, $"CREATE DATABASE {name}");
        Execute(adminConnection, $"CREATE ROLE {role} LOGIN PASSWORD '{RolePassword}' NOSUPERUSER NOCREATEDB NOCREATEROLE");
        Execute(adminConnection, $"GRANT CONNECT ON DATABASE {name} TO {role}");
        adminDatabaseConnection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = name }.ConnectionString;
        roleConnection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = name, Username = role, Password = RolePassword }.ConnectionString;
    }


    /// <summary>
    /// entity manager connected as the superuser
    /// </summary>
    public IEntityManager AdminEntities => Entities(adminDatabaseConnection);


    /// <summary>
    /// entity manager connected as the role which is neither owner nor superuser
    /// </summary>
    public IEntityManager RoleEntities => Entities(roleConnection);


    static IEntityManager Entities(string connectionString) => new EntityManager(ClientFactory.Create(() => new NpgsqlConnection(connectionString), new PostgreInfo(), true));


    static void Execute(string connectionString, string sql) {
        using NpgsqlConnection connection = new(connectionString);
        connection.Open();
        using NpgsqlCommand command = new(sql, connection);
        command.ExecuteNonQuery();
    }


    /// <summary>
    /// denies the role the right to create objects in the database, which makes creating an extension fail deterministically
    /// </summary>
    public void RevokeDatabaseCreate() => Execute(adminConnection, $"REVOKE CREATE ON DATABASE {name} FROM PUBLIC");


    /// <summary>
    /// enables the vector extension as the superuser
    /// </summary>
    public void EnableExtension() => Execute(adminDatabaseConnection, "CREATE EXTENSION vector");


    /// <summary>
    /// lets the role create tables in the public schema
    /// </summary>
    public void GrantSchema() => Execute(adminDatabaseConnection, $"GRANT USAGE, CREATE ON SCHEMA public TO {role}");


    /// <summary>
    /// executes a query as the superuser and returns the first column of the first row
    /// </summary>
    public T Scalar<T>(string sql) {
        using NpgsqlConnection connection = new(adminDatabaseConnection);
        connection.Open();
        using NpgsqlCommand command = new(sql, connection);
        return (T)command.ExecuteScalar()!;
    }


    /// <summary>
    /// runs the database model service against the given entity manager
    /// </summary>
    public static Task Start(IEntityManager entities) => new DatabaseModelService(entities, NullLogger<DatabaseModelService>.Instance).StartAsync(CancellationToken.None);


    /// <inheritdoc />
    public void Dispose() {
        NpgsqlConnection.ClearAllPools();
        Execute(adminConnection, $"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
        Execute(adminConnection, $"DROP ROLE IF EXISTS {role}");
    }
}
