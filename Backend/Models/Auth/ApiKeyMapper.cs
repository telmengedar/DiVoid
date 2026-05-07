using Pooshit.Json;
using Pooshit.Ocelot.Fields;

namespace Backend.Models.Auth;

/// <summary>
/// mapper for <see cref="ApiKey"/>s
/// </summary>
public class ApiKeyMapper : FieldMapper<ApiKeyDetails, ApiKey> {

    /// <summary>
    /// creates a new <see cref="ApiKeyMapper"/>
    /// </summary>
    public ApiKeyMapper()
        : base(Mappings()) {
    }

    static IEnumerable<FieldMapping<ApiKeyDetails>> Mappings() {
        yield return new FieldMapping<ApiKeyDetails, long>("id",
                                                           k => k.Id,
                                                           (k, v) => k.Id = v);
        yield return new FieldMapping<ApiKeyDetails, string>("keyid",
                                                             k => k.KeyId,
                                                             (k, v) => k.KeyId = v);
        yield return new FieldMapping<ApiKeyDetails, string>("permissions",
                                                             k => k.Permissions,
                                                             (k, v) => k.Permissions = string.IsNullOrEmpty(v) ? [] : Json.Read<string[]>(v));
        yield return new FieldMapping<ApiKeyDetails, long?>("user.id",
                                                            k => k.UserId,
                                                            (k, v) => k.UserId = v);
        yield return new FieldMapping<ApiKeyDetails, bool>("enabled",
                                                           k => k.Enabled,
                                                           (k, v) => k.Enabled = v);
        yield return new FieldMapping<ApiKeyDetails, DateTime>("createdat",
                                                               k => k.CreatedAt,
                                                               (k, v) => k.CreatedAt = v);
        yield return new FieldMapping<ApiKeyDetails, DateTime?>("lastusedat",
                                                                k => k.LastUsedAt,
                                                                (k, v) => k.LastUsedAt = v);
        yield return new FieldMapping<ApiKeyDetails, DateTime?>("expiresat",
                                                                k => k.ExpiresAt,
                                                                (k, v) => k.ExpiresAt = v);
    }
}
