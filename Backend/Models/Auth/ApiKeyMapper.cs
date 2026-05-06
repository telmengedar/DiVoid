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
		yield return new FieldMapping<ApiKeyDetails, string>("key",
		                                                   k => k.Key,
		                                                   (k, v) => k.Key = v);
		yield return new FieldMapping<ApiKeyDetails, string>("permissions",
		                                                     k => k.Permissions,
		                                                     (k, v) => k.Permissions = string.IsNullOrEmpty(v) ? [] : Json.Read<string[]>(v));
		yield return new FieldMapping<ApiKeyDetails, long?>("customer.id",
		                                                    k => k.CustomerId,
		                                                    (k, v) => k.CustomerId = v);
	}
}