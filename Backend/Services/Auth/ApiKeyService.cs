using Backend.Extensions;
using Backend.Models.Auth;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Errors.Exceptions;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Json;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Tokens;

namespace Backend.Services.Auth;

/// <inheritdoc />
public class ApiKeyService : IApiKeyService {
	readonly IEntityManager database;
	readonly IKeyGenerator keyGenerator;

	/// <summary>
	/// creates a new <see cref="ApiKeyService"/>
	/// </summary>
	/// <param name="database">access to database</param>
	/// <param name="keyGenerator">generator for random keys</param>
	public ApiKeyService(IEntityManager database, IKeyGenerator keyGenerator) {
		this.database = database;
		this.keyGenerator = keyGenerator;
	}

	/// <inheritdoc />
	public async Task<ApiKeyDetails> CreateApiKey(ApiKeyParameters apiKey) {
		string key = keyGenerator.GenerateKey(16);

		return new() {
			Id = await database.Insert<ApiKey>()
			                   .Columns(k => k.Key, k => k.Permissions, k => k.UserId)
			                   .Values(key, Json.WriteString(apiKey.Permissions), apiKey.UserId)
			                   .ReturnID()
			                   .ExecuteAsync(),
			Key = key,
			Permissions = apiKey.Permissions,
			UserId = apiKey.UserId
		};
	}

	/// <inheritdoc />
	public async Task<ApiKeyDetails> GetApiKey(string apiKey) {
		ApiKeyMapper mapper = new();

		ApiKeyDetails key = await mapper.EntityFromOperation(mapper.CreateOperation(database).Where(k => k.Key == apiKey));
		if (key == null)
			throw new NotFoundException<ApiKey>(apiKey);
		return key;
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
		if (await database.Delete<ApiKey>().Where(k => k.Id == keyId).ExecuteAsync() == 0)
			throw new NotFoundException<ApiKey>(keyId);
	}
}