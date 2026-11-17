using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Backend.Errors.Exceptions;
using Backend.Models.Nodes;
using Backend.Models.Users;
using Pooshit.Json;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Fields;
using Pooshit.Ocelot.Tokens;

namespace Backend.Services.Teams;

/// <summary>Resolves one protected, cryptographically authorized graph binding per incoming message.</summary>
public sealed class TeamsGraphBindingService : ITeamsGraphBindingService {
    readonly IEntityManager database;
    readonly TeamsRoutingOptions options;
    readonly byte[] proofKey;

    /// <summary>Creates the direct server graph reader.</summary>
    /// <param name="database">The application persistence boundary.</param>
    /// <param name="options">The trusted server bot identity.</param>
    /// <param name="configuration">The existing active bot credential source.</param>
    public TeamsGraphBindingService(IEntityManager database, TeamsRoutingOptions options, IConfiguration configuration) {
        this.database = database;
        this.options = options;
        proofKey = Encoding.UTF8.GetBytes(configuration["AzureAd:ClientCredentials:0:ClientSecret"] ?? "");
    }

    /// <inheritdoc />
    public async Task<(long RecipientId, string Tag)> ResolveAsync(Transaction transaction, CancellationToken cancellationToken) {
        ISqlToken length = DB.CustomFunction("length", DB.Property<Node>(node => node.Content));
        ISqlToken bounded = DB.Case([DB.When(DB.Predicate<Node>(node =>
            DB.CustomFunction("length", DB.Property<Node>(value => value.Content)).Single <= 8192),
            DB.Property<Node>(node => node.Content))], DB.Constant(null));
        IDBField[] fields = [(IDBField)DB.Property<Node>(node => node.Id), (IDBField)DB.Property<Node>(node => node.OwnerId),
            (IDBField)DB.Property<Node>(node => node.Access), (IDBField)DB.As(length, "ContentBytes"),
            (IDBField)DB.As(bounded, "BoundedContent")];
        int candidates = 0;
        int authorized = 0;
        long owner = 0;
        NodeAccess access = NodeAccess.None;
        Dictionary<string, object> record = null;
        using (Reader reader = await database.Load<Node>(fields)
            .Join<NodeType>((node, type) => node.TypeId == type.Id, "bindingType")
            .Where((node, type) => node.Name == options.BotAppId && type.Type == "teams-bot-binding")
            .Limit(129).ExecuteReaderAsync(transaction)) {
            while (await reader.ReadAsync(cancellationToken)) {
                if (++candidates > 128 || (!reader.IsDBNull(3) && Convert.ToInt64(reader[3]) > 8192))
                    throw Unavailable();
                if (reader.IsDBNull(4))
                    continue;
                byte[] content = (byte[])reader[4];
                Dictionary<string, object> fieldsRead;
                try {
                    fieldsRead = Json.Read<Dictionary<string, object>>(new UTF8Encoding(false, true).GetString(content));
                } catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException or InvalidCastException) {
                    continue;
                }
                long nodeId = reader.GetInt64(0);
                long ownerId = reader.GetInt64(1);
                if (fieldsRead == null || !fieldsRead.TryGetValue("proof", out object proof) ||
                    proof is not string signed || !ValidProof(signed, nodeId, ownerId))
                    continue;
                authorized++;
                owner = ownerId;
                access = (NodeAccess)Convert.ToInt32(reader[2]);
                record = fieldsRead;
            }
        }
        if (authorized != 1 || (access & NodeAccess.Write) != 0 || owner <= 0 ||
            !record.TryGetValue("botId", out object bot) || bot is not string botId || botId != options.BotAppId ||
            !record.TryGetValue("userId", out object target) || !record.TryGetValue("tag", out object rawTag) ||
            rawTag is not string tag || string.IsNullOrWhiteSpace(tag) || tag.Length > 128 ||
            tag.Any(character => char.IsWhiteSpace(character) || "[],".Contains(character)))
            throw Unavailable();
        long recipient = target switch { long id => id, int id => id, _ => 0 };
        if (recipient <= 0 ||
            await database.Load<User>(DB.Count()).Where(user => user.Id == owner && user.Enabled).ExecuteScalarAsync<long>(transaction) != 1 ||
            await database.Load<User>(DB.Count()).Where(user => user.Id == recipient && user.Enabled).ExecuteScalarAsync<long>(transaction) != 1)
            throw Unavailable();
        return (recipient, tag);
    }

    bool ValidProof(string proof, long nodeId, long ownerId) {
        const string ProofPrefix = "hmac-sha256-v1:";
        if (nodeId <= 0 || ownerId <= 0 || proofKey.Length == 0 || proof.Length != ProofPrefix.Length + 64 ||
            !proof.StartsWith(ProofPrefix, StringComparison.Ordinal) ||
            proof[ProofPrefix.Length..].Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            return false;
        string message = "DiVoid/TeamsGraphBinding/v1\n" + options.BotAppId + "\n" +
            nodeId.ToString(CultureInfo.InvariantCulture) + "\n" + ownerId.ToString(CultureInfo.InvariantCulture) + "\n";
        byte[] expected = HMACSHA256.HashData(proofKey, Encoding.UTF8.GetBytes(message));
        return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(proof[ProofPrefix.Length..]));
    }

    static MessageReplyException Unavailable() => new(HttpStatusCode.ServiceUnavailable,
        "teams_binding_unavailable", "One safe, authorized Teams graph binding is required.");
}
