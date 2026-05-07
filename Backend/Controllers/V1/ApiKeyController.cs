using Backend.Models.Auth;
using Backend.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.AspNetCore.Services.Patches;

namespace Backend.Controllers.V1;

/// <summary>
/// controller used to manage api keys
/// </summary>
[Route("api/apikeys")]
[ApiController]
[Authorize(Policy = "admin")]
public class ApiKeyController(ILogger<ApiKeyController> logger, IApiKeyService apiKeyService) : ControllerBase {
    readonly ILogger<ApiKeyController> logger = logger;
    readonly IApiKeyService apiKeyService = apiKeyService;

    /// <summary>
    /// creates a new api key
    /// </summary>
    /// <param name="parameters">parameters for the key to create</param>
    /// <returns>created key details including the plaintext key (returned once only)</returns>
    [HttpPost]
    public Task<ApiKeyDetails> CreateApiKey([FromBody] ApiKeyParameters parameters) {
        logger.LogInformation("Creating api key for user {UserId}", parameters.UserId);
        return apiKeyService.CreateApiKey(parameters);
    }

    /// <summary>
    /// get an api key by its row id
    /// </summary>
    /// <param name="keyId">row id of the api key</param>
    /// <returns>api key details (no plaintext key)</returns>
    [HttpGet("{keyId:long}")]
    public Task<ApiKeyDetails> GetApiKeyById(long keyId) => apiKeyService.GetApiKeyById(keyId);

    /// <summary>
    /// lists existing api keys
    /// </summary>
    /// <param name="filter">paging and field filter</param>
    /// <returns>page of api key details</returns>
    [HttpGet]
    public Task<AsyncPageResponseWriter<ApiKeyDetails>> ListApiKeys([FromQuery] ListFilter filter) => Task.FromResult(apiKeyService.ListApiKeys(filter));

    /// <summary>
    /// patches an existing api key (e.g. revoke by setting enabled=false)
    /// </summary>
    /// <param name="keyId">id of key to patch</param>
    /// <param name="patches">patches to apply</param>
    /// <returns>updated api key details</returns>
    [HttpPatch("{keyId:long}")]
    public Task<ApiKeyDetails> PatchApiKey(long keyId, [FromBody] PatchOperation[] patches) {
        logger.LogInformation("Patching api key {KeyId}", keyId);
        return apiKeyService.UpdateApiKey(keyId, patches);
    }

    /// <summary>
    /// deletes an api key
    /// </summary>
    /// <param name="keyId">id of key to delete</param>
    [HttpDelete("{keyId:long}")]
    public Task DeleteApiKey(long keyId) {
        logger.LogInformation("Deleting api key {KeyId}", keyId);
        return apiKeyService.DeleteApiKey(keyId);
    }
}
