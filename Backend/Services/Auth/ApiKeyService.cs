using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Backend.Extensions;
using Backend.Models.Auth;
using Backend.Services.Users;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Errors.Exceptions;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Json;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Expressions;
using Pooshit.Ocelot.Tokens;

namespace Backend.Services.Auth;

/// <inheritdoc />
public class ApiKeyService : IApiKeyService {
    readonly IEntityManager database;
    readonly IKeyGenerator keyGenerator;
    readonly ILogger<ApiKeyService> logger;
    readonly byte[] pepper;

    /// <summary>
    /// creates a new <see cref="ApiKeyService"/>
    /// </summary>
    /// <param name="database">access to database</param>
    /// <param name="keyGenerator">generator for random keys</param>
    /// <param name="configuration">application configuration (reads DIVOID_KEY_PEPPER)</param>
    /// <param name="logger">logger</param>
    public ApiKeyService(IEntityManager database, IKeyGenerator keyGenerator, IConfiguration configuration, ILogger<ApiKeyService> logger) {
        this.database = database;
        this.keyGenerator = keyGenerator;
        this.logger = logger;

        string pepperValue = configuration["DIVOID_KEY_PEPPER"];
        bool authEnabled = configuration.GetValue("Auth:Enabled", true);

        if (string.IsNullOrEmpty(pepperValue) || Encoding.UTF8.GetByteCount(pepperValue) < 32) {
            if (authEnabled)
                throw new MissingPepperException(
                    "DIVOID_KEY_PEPPER is unset or shorter than 32 bytes. The service will not start with Auth:Enabled=true without a valid pepper.");
            logger.LogInformation("DIVOID_KEY_PEPPER is not set; using dev placeholder (Auth:Enabled=false). Set the pepper before enabling auth.");
            pepperValue = "dev-placeholder-pepper-not-for-production-use-0000000";
        }

        pepper = Encoding.UTF8.GetBytes(pepperValue);
    }

    byte[] ComputeHmac(string fullKey) {
        using HMACSHA256 hmac = new(pepper);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(fullKey));
    }

    static bool ConstantTimeEquals(byte[] a, byte[] b) {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++)
            diff |= a[i] ^ b[i];
        return diff == 0;
    }

    static readonly string[] ValidPermissions = ["admin", "read", "write"];

    static void ValidatePermissions(string[] permissions) {
        if (permissions == null || permissions.Length == 0)
            throw new ArgumentException("Permissions must be a non-empty array. Allowed values: admin, read, write.");

        foreach (string permission in permissions) {
            bool valid = false;
            foreach (string allowed in ValidPermissions) {
                if (allowed == permission) { valid = true; break; }
            }
            if (!valid)
                throw new ArgumentException($"Unknown permission '{permission}'. Allowed values: admin, read, write.");
        }
    }

    /// <inheritdoc />
    public async Task<ApiKeyDetails> CreateApiKey(ApiKeyParameters apiKey) {
        ValidatePermissions(apiKey.Permissions);

        string keyId = keyGenerator.GenerateKey(2);   // 2 * 6 = 12 chars
        string secret = keyGenerator.GenerateKey(4);  // 4 * 6 = 24 chars (~120 bits)
        string fullKey = $"{keyId}.{secret}";
        byte[] hash = ComputeHmac(fullKey);
        DateTime now = DateTime.UtcNow;

        long id = await database.Insert<ApiKey>()
                                .Columns(k => k.UserId, k => k.KeyId, k => k.KeyHash, k => k.Permissions, k => k.Enabled, k => k.CreatedAt)
                                .Values(apiKey.UserId ?? 0, keyId, hash, Json.WriteString(apiKey.Permissions), true, now)
                                .ReturnID()
                                .ExecuteAsync();

        logger.LogInformation("event=apikey.created keyId={KeyId} userId={UserId} permissions={Permissions}",
            keyId, apiKey.UserId, string.Join(",", apiKey.Permissions ?? []));

        return new() {
            Id = id,
            KeyId = keyId,
            Permissions = apiKey.Permissions,
            Enabled = true,
            CreatedAt = now,
            UserId = apiKey.UserId,
            PlaintextKey = fullKey
        };
    }

    /// <inheritdoc />
    public async Task<ApiKeyDetails> GetApiKey(string fullKey) {
        if (string.IsNullOrEmpty(fullKey))
            throw new NotFoundException<ApiKey>("(empty)");

        int dotIndex = fullKey.IndexOf('.');
        if (dotIndex < 0)
            throw new NotFoundException<ApiKey>(fullKey);

        string keyId = fullKey.Substring(0, dotIndex);

        ApiKey row = await database.Load<ApiKey>()
                                   .Where(k => k.KeyId == keyId)
                                   .ExecuteEntityAsync();
        if (row == null)
            throw new NotFoundException<ApiKey>(keyId);

        byte[] expected = ComputeHmac(fullKey);
        if (!ConstantTimeEquals(expected, row.KeyHash))
            throw new NotFoundException<ApiKey>(keyId);

        if (!row.Enabled)
            throw new InvalidOperationException("disabled_key");
        if (row.ExpiresAt.HasValue && row.ExpiresAt.Value < DateTime.UtcNow)
            throw new InvalidOperationException("expired");

        // Check parent user
        User user = await database.Load<User>()
                                  .Where(u => u.Id == row.UserId)
                                  .ExecuteEntityAsync();
        if (user == null || !user.Enabled)
            throw new InvalidOperationException("disabled_user");

        // Fire-and-forget LastUsedAt update
        long rowId = row.Id;
        _ = Task.Run(async () => {
            try {
                DateTime usedAt = DateTime.UtcNow;
                await database.Update<ApiKey>()
                              .Set(k => k.LastUsedAt == usedAt)
                              .Where(k => k.Id == rowId)
                              .ExecuteAsync();
            } catch { /* best-effort */ }
        });

        return BuildDetails(row);
    }

    /// <inheritdoc />
    public async Task<ApiKeyDetails> GetApiKeyById(long keyId) {
        ApiKeyMapper mapper = new();
        ApiKeyDetails key = await mapper.EntityFromOperation(mapper.CreateOperation(database).Where(k => k.Id == keyId));
        if (key == null)
            throw new NotFoundException<ApiKey>(keyId);
        return key;
    }

    /// <inheritdoc />
    public AsyncPageResponseWriter<ApiKeyDetails> ListApiKeys(ListFilter filter = null) {
        filter ??= new();
        ApiKeyMapper mapper = new();
        return new(
            mapper.EntitiesFromOperation(mapper.CreateOperation(database, filter.Fields)),
            () => mapper.CreateOperation(database, DB.Count()).ExecuteScalarAsync<long>(),
            filter.Continue
        );
    }

    /// <inheritdoc />
    public async Task<ApiKeyDetails> UpdateApiKey(long keyId, params PatchOperation[] patches) {
        if (await database.Update<ApiKey>()
                          .Patch(patches)
                          .Where(k => k.Id == keyId)
                          .ExecuteAsync() == 0)
            throw new NotFoundException<ApiKey>(keyId);
        return await GetApiKeyById(keyId);
    }

    /// <inheritdoc />
    public async Task DeleteApiKey(long keyId) {
        ApiKeyDetails key = await GetApiKeyById(keyId);
        if (await database.Delete<ApiKey>().Where(k => k.Id == keyId).ExecuteAsync() == 0)
            throw new NotFoundException<ApiKey>(keyId);
        logger.LogInformation("event=apikey.deleted keyId={KeyId}", key.KeyId);
    }

    /// <inheritdoc />
    public async Task<bool> AnyAdminKeyExists() {
        IAsyncEnumerable<ApiKey> keys = database.Load<ApiKey>()
                                                .Where(k => k.Enabled == true)
                                                .ExecuteEntitiesAsync();
        await foreach (ApiKey key in keys) {
            if (string.IsNullOrEmpty(key.Permissions)) continue;
            string[] perms = Json.Read<string[]>(key.Permissions);
            foreach (string p in perms) {
                if (p == "admin") return true;
            }
        }
        return false;
    }

    static ApiKeyDetails BuildDetails(ApiKey row) {
        return new() {
            Id = row.Id,
            KeyId = row.KeyId,
            UserId = row.UserId,
            Permissions = string.IsNullOrEmpty(row.Permissions) ? [] : Json.Read<string[]>(row.Permissions),
            Enabled = row.Enabled,
            CreatedAt = row.CreatedAt,
            LastUsedAt = row.LastUsedAt,
            ExpiresAt = row.ExpiresAt
        };
    }
}
