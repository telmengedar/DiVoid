using Backend.Models.Auth;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.AspNetCore.Services.Patches;

namespace apikeyservice.Services;

/// <summary>
/// service used to manage api keys
/// </summary>
public interface IApiKeyService {

	/// <summary>
	/// creates a new <see cref="ApiKey"/>
	/// </summary>
	/// <param name="apiKey">data for api key to generate</param>
	/// <returns>created api key</returns>
	Task<ApiKeyDetails> CreateApiKey(ApiKeyParameters apiKey);
	
	/// <summary>
	/// get data for an existing api key
	/// </summary>
	/// <param name="apiKey">api key to get</param>
	/// <returns>data of api key</returns>
	Task<ApiKeyDetails> GetApiKey(string apiKey);

	/// <summary>
	/// get data for an existing api key by id
	/// </summary>
	/// <param name="keyId">id of api key to get</param>
	/// <returns>data of api key</returns>
	Task<ApiKeyDetails> GetApiKeyById(long keyId);

	/// <summary>
	/// lists existing api keys
	/// </summary>
	/// <param name="filter">filter for api keys</param>
	/// <returns>page of api keys</returns>
	AsyncPageResponseWriter<ApiKeyDetails> ListApiKeys(ListFilter filter = null);

	/// <summary>
	/// updates properties of an existing <see cref="ApiKey"/>
	/// </summary>
	/// <param name="keyId"></param>
	/// <param name="patches"></param>
	/// <returns></returns>
	Task<ApiKeyDetails> UpdateApiKey(long keyId, params PatchOperation[] patches);
	
	/// <summary>
	/// deletes an existing api key
	/// </summary>
	/// <param name="keyId">id of key to delete</param>
	Task DeleteApiKey(long keyId);
}