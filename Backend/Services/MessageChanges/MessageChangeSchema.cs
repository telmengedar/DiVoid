using Backend.Models.Auth;
using Backend.Models.Messages;
using Backend.Models.Users;
using Npgsql;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Entities.Descriptors;
using Pooshit.Ocelot.Info;

namespace Backend.Services.MessageChanges;

internal static class MessageChangeSchema {
    internal static Task LockAsync(IEntityManager database, Transaction transaction) => database.DBClient.DBInfo is PostgreInfo
        ? database.DBClient.NonQueryAsync(transaction, "SELECT pg_advisory_xact_lock(1145656911,34331)") : Task.CompletedTask;

    internal static async Task InstallAsync(IEntityManager database, Transaction transaction) {
        if (database.DBClient.DBInfo is not PostgreInfo)
            return;
        NpgsqlConnection connection = (NpgsqlConnection)transaction.DbTransaction.Connection;
        NpgsqlTransaction native = (NpgsqlTransaction)transaction.DbTransaction;
        await InstallTableAsync(await ResolveAsync<Message>(database, connection, native), connection, native, "message", ["RecipientId"]);
        await InstallTableAsync(await ResolveAsync<ApiKey>(database, connection, native), connection, native, "key", ["UserId", "Enabled", "Permissions", "ExpiresAt", "KeyId", "KeyHash"]);
        await InstallTableAsync(await ResolveAsync<User>(database, connection, native), connection, native, "user", ["Enabled", "Permissions"]);
    }

    internal static string Channel(string kind, long oid, long id) => $"divoid_inbox_v1_{kind}_{oid}_{id}";

    internal static string Quote(string identifier) => new NpgsqlCommandBuilder().QuoteIdentifier(identifier);

    internal static async Task<MessageChangeTable> ResolveAsync<T>(IEntityManager database, NpgsqlConnection connection, NpgsqlTransaction transaction = null) {
        EntityDescriptor descriptor = database.Model<T>().Descriptor;
        await using NpgsqlCommand command = new("SELECT c.oid::bigint,n.nspname,c.relname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE c.oid=to_regclass(@table)", connection, transaction);
        command.Parameters.AddWithValue("table", database.DBClient.DBInfo.MaskColumn(descriptor.TableName));
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException("The inbox notification table is unavailable.");
        return new MessageChangeTable(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), descriptor);
    }

    static async Task InstallTableAsync(MessageChangeTable table, NpgsqlConnection connection, NpgsqlTransaction transaction, string kind, string[] properties) {
        string qualified = Quote(table.Schema) + "." + Quote(table.Name);
        string function = Quote(table.Schema) + "." + Quote("divoid_inbox_v1_" + kind + "_" + table.Oid);
        string field = Quote(table.Descriptor.GetColumnByProperty(kind == "message" ? "RecipientId" : "Id").Name);
        string predicate = "";
        if (kind != "message") {
            string before = string.Join(",", properties.Select(property => "OLD." + Quote(table.Descriptor.GetColumnByProperty(property).Name)));
            string after = string.Join(",", properties.Select(property => "NEW." + Quote(table.Descriptor.GetColumnByProperty(property).Name)));
            predicate = $"IF TG_OP='UPDATE' AND ROW({before}) IS NOT DISTINCT FROM ROW({after}) THEN RETURN NULL; END IF;";
        }
        string sql = $"""
            CREATE OR REPLACE FUNCTION {function}() RETURNS trigger LANGUAGE plpgsql AS $divoid$
            DECLARE actor bigint;
            BEGIN
              {predicate}
              IF TG_OP='DELETE' THEN actor:=OLD.{field}; ELSE actor:=NEW.{field}; END IF;
              PERFORM pg_catalog.pg_notify('divoid_inbox_v1_{kind}_' || TG_RELID::text || '_' || actor::text,'');
              RETURN NULL;
            END;
            $divoid$;
            DROP TRIGGER IF EXISTS divoid_inbox_v1 ON {qualified};
            CREATE TRIGGER divoid_inbox_v1 AFTER { (kind == "message" ? "INSERT OR DELETE" : "UPDATE OR DELETE") } ON {qualified} FOR EACH ROW EXECUTE FUNCTION {function}();
            """;
        await using NpgsqlCommand command = new(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task<NpgsqlConnection> OpenAsync(IEntityManager database, CancellationToken cancellationToken) {
        using IConnection borrowed = await database.DBClient.Connection.ConnectAsync();
        NpgsqlConnection original = (NpgsqlConnection)borrowed.Connection;
        NpgsqlConnectionStringBuilder settings = new(original.ConnectionString) {
            Pooling = false, Multiplexing = false, Enlist = false, KeepAlive = 0, Timeout = 15, CommandTimeout = 15,
            ApplicationName = "divoid-inbox-events"
        };
        NpgsqlConnection connection = original.CloneWith(settings.ConnectionString);
        try {
            await connection.OpenAsync(cancellationToken);
            return connection;
        } catch {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal static async Task RequireInstalledAsync(NpgsqlConnection connection, MessageChangeTable[] tables) {
        foreach (MessageChangeTable table in tables) {
            string kind = table.Descriptor.Columns.Any(column => column.Property.Name == nameof(Message.RecipientId)) ? "message" :
                table.Descriptor.Columns.Any(column => column.Property.Name == nameof(ApiKey.KeyHash)) ? "key" : "user";
            await using NpgsqlCommand command = new("SELECT count(*) FROM pg_trigger t JOIN pg_proc p ON p.oid=t.tgfoid JOIN pg_class c ON c.oid=t.tgrelid WHERE t.tgrelid=@oid::oid AND t.tgname='divoid_inbox_v1' AND t.tgenabled IN ('O','A') AND NOT t.tgisinternal AND p.pronamespace=c.relnamespace AND p.proname=@function", connection);
            command.Parameters.AddWithValue("oid", table.Oid);
            command.Parameters.AddWithValue("function", "divoid_inbox_v1_" + kind + "_" + table.Oid);
            if (Convert.ToInt64(await command.ExecuteScalarAsync()) != 1)
                throw new InvalidOperationException("The inbox notification producer is unavailable.");
        }
    }
}
