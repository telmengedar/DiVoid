using Backend.Models.Auth;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.AspNetCore.Services.Patches;

namespace Backend.Services.Auth;

/// <summary>
/// service used to manage api keys
/// </summary>
public interface IApiKeyService {

    /// <summary>
    /// creates a new <see cref="ApiKey"/>
    /// </summary>
    /// <param name="apiKey">data for api key to generate</param>
    /// <returns>created api key details, including the plaintext key (returned once only)</returns>
    Task<ApiKeyDetails> CreateApiKey(ApiKeyParameters apiKey);

    /// <summary>
    /// looks up an api key by its full plaintext value and validates it
    /// </summary>
    /// <param name="fullKey">full key string in format &lt;keyId&gt;.&lt;secret&gt;</param>
    /// <returns>details of the matching key; throws if not found, disabled, or expired</returns>
    Task<ApiKeyDetails> GetApiKey(string fullKey);

    /// <summary>
    /// get data for an existing api key by its row id
    /// </summary>
    /// <param name="keyId">row id of api key to get</param>
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
    /// <param name="keyId">id of key to update</param>
    /// <param name="patches">patches to apply</param>
    /// <returns>updated api key details</returns>
    Task<ApiKeyDetails> UpdateApiKey(long keyId, params PatchOperation[] patches);

    /// <summary>
    /// deletes an existing api key
    /// </summary>
    /// <param name="keyId">id of key to delete</param>
    Task DeleteApiKey(long keyId);

    /// <summary>
    /// determines whether any admin api key exists
    /// </summary>
    /// <returns>true if at least one enabled admin api key exists</returns>
    Task<bool> AnyAdminKeyExists();
}
