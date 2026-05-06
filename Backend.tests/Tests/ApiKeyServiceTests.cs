using apikeyservice.Services;
using Backend.Models.Auth;
using Backend.Services.Auth;
using Backend.tests.Fixtures;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Errors.Exceptions;
using Pooshit.AspNetCore.Services.Patches;

namespace Backend.tests.Tests;

/// <summary>
/// Tests for <see cref="ApiKeyService"/>.
///
/// Bug 17 (fixed): ApiKeyMapper had a FieldMapping keyed <c>"customer.id"</c> mapping
/// to a <c>customerid</c> column that did not exist — the table only has <c>userid</c>.
/// Fix: renamed key to <c>"user.id"</c>, expression to <c>k.UserId</c>, and renamed
/// <c>ApiKeyDetails.CustomerId</c> → <c>ApiKeyDetails.UserId</c> to match.
/// </summary>
[TestFixture]
public class ApiKeyServiceTests
{
    static ApiKeyService MakeService(DatabaseFixture fixture)
        => new(fixture.EntityManager, new KeyGenerator());

    // -----------------------------------------------------------------------
    // CreateApiKey — does NOT go through ApiKeyMapper, so it works fine.
    // -----------------------------------------------------------------------

    [Test]
    public async Task CreateApiKey_PersistsRecord_ReturnsIdAndKey()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);

        ApiKeyDetails result = await svc.CreateApiKey(new ApiKeyParameters
        {
            UserId = 7,
            Permissions = ["read", "write"]
        });

        Assert.Multiple(() => {
            Assert.That(result.Id, Is.GreaterThan(0));
            Assert.That(result.Key, Is.Not.Empty);
            Assert.That(result.Permissions, Is.EqualTo(new[] { "read", "write" }));
            Assert.That(result.UserId, Is.EqualTo(7));
        });
    }

    [Test]
    public async Task CreateApiKey_PermissionsAreJsonEncoded()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);

        ApiKeyDetails result = await svc.CreateApiKey(new ApiKeyParameters
        {
            UserId = 1,
            Permissions = ["admin"]
        });

        // Verify the raw DB row has JSON-encoded permissions (bypass ApiKeyMapper).
        ApiKey? raw = await fixture.EntityManager.Load<ApiKey>()
                                   .Where(k => k.Id == result.Id)
                                   .ExecuteEntityAsync();
        Assert.That(raw!.Permissions, Does.StartWith("["));
    }

    // -----------------------------------------------------------------------
    // GetApiKey / GetApiKeyById — go through ApiKeyMapper (bug 17 fixed).
    // -----------------------------------------------------------------------

    [Test]
    public async Task GetApiKey_ExistingKey_ReturnsDetails()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);
        ApiKeyDetails created = await svc.CreateApiKey(new ApiKeyParameters { UserId = 1, Permissions = [] });

        ApiKeyDetails fetched = await svc.GetApiKey(created.Key!);

        Assert.That(fetched.Id, Is.EqualTo(created.Id));
        await Task.CompletedTask;
    }

    [Test]
    public void GetApiKey_MissingKey_ThrowsNotFoundException()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);

        Assert.ThrowsAsync<NotFoundException<ApiKey>>(
            () => svc.GetApiKey("definitely-does-not-exist"));
    }

    [Test]
    public async Task GetApiKeyById_ExistingId_ReturnsDetails()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);
        ApiKeyDetails created = await svc.CreateApiKey(new ApiKeyParameters { UserId = 1, Permissions = [] });

        ApiKeyDetails fetched = await svc.GetApiKeyById(created.Id);

        Assert.That(fetched.Key, Is.EqualTo(created.Key));
        await Task.CompletedTask;
    }

    [Test]
    public void GetApiKeyById_MissingId_ThrowsNotFoundException()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);

        Assert.ThrowsAsync<NotFoundException<ApiKey>>(
            () => svc.GetApiKeyById(9999));
    }

    // -----------------------------------------------------------------------
    // UpdateApiKey — Patch itself works (tested in DatabasePatchExtensionsTests);
    // return value read goes through ApiKeyMapper (bug 17 fixed).
    // -----------------------------------------------------------------------

    [Test]
    public async Task UpdateApiKey_PatchesAllowedField_ReturnsUpdatedDetails()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);
        ApiKeyDetails created = await svc.CreateApiKey(new ApiKeyParameters
        {
            UserId = 1,
            Permissions = []
        });

        ApiKeyDetails updated = await svc.UpdateApiKey(
            created.Id,
            new PatchOperation { Op = "replace", Path = "/userid", Value = 99L });

        Assert.That(updated.UserId, Is.EqualTo(99));
        await Task.CompletedTask;
    }

    [Test]
    public void UpdateApiKey_MissingId_ThrowsNotFoundException()
    {
        // This throws NotFoundException before reaching the mapper read.
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);

        Assert.ThrowsAsync<NotFoundException<ApiKey>>(
            () => svc.UpdateApiKey(9999, new PatchOperation { Op = "replace", Path = "/userid", Value = 1L }));
    }

    // -----------------------------------------------------------------------
    // DeleteApiKey — Delete itself works; the verification path uses the mapper.
    // -----------------------------------------------------------------------

    [Test]
    public async Task DeleteApiKey_ExistingKey_RemovesRecord()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);
        ApiKeyDetails created = await svc.CreateApiKey(new ApiKeyParameters { UserId = 1, Permissions = [] });

        await svc.DeleteApiKey(created.Id);

        // Verify via direct DB query rather than through the broken mapper.
        long count = await fixture.EntityManager.Load<ApiKey>(Pooshit.Ocelot.Tokens.DB.Count())
                                  .Where(k => k.Id == created.Id)
                                  .ExecuteScalarAsync<long>();
        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public void DeleteApiKey_MissingId_ThrowsNotFoundException()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);

        Assert.ThrowsAsync<NotFoundException<ApiKey>>(
            () => svc.DeleteApiKey(9999));
    }

    // -----------------------------------------------------------------------
    // ListApiKeys — mapper is used for serialisation (bug 17 fixed).
    // -----------------------------------------------------------------------

    [Test]
    public async Task ListApiKeys_ReturnsCreatedKeys()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);

        await svc.CreateApiKey(new ApiKeyParameters { UserId = 1, Permissions = [] });
        await svc.CreateApiKey(new ApiKeyParameters { UserId = 1, Permissions = [] });

        var writer = svc.ListApiKeys(new ListFilter { Count = 10 });
        byte[] buffer;
        using (MemoryStream ms = new()) {
            await writer.Write(ms);
            buffer = ms.ToArray();
        }
        Assert.That(buffer.Length, Is.GreaterThan(0));
        await Task.CompletedTask;
    }
}
