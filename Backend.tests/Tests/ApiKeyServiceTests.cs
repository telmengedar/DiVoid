using System;
using System.Text;
using System.Security.Cryptography;
using Backend.Models.Auth;
using Backend.Services.Auth;
using Backend.tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Errors.Exceptions;
using Pooshit.AspNetCore.Services.Patches;

namespace Backend.tests.Tests;

/// <summary>
/// Tests for <see cref="ApiKeyService"/> with HMAC-SHA-256 storage.
/// </summary>
[TestFixture]
public class ApiKeyServiceTests
{
    const string TestPepper = "test-pepper-value-that-is-at-least-32-bytes-long-0000";

    static ApiKeyService MakeService(DatabaseFixture fixture, string pepper = TestPepper)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["DIVOID_KEY_PEPPER"] = pepper,
                ["Auth:Enabled"] = "true"
            })
            .Build();
        return new(fixture.EntityManager, new KeyGenerator(), config, NullLogger<ApiKeyService>.Instance);
    }

    // -----------------------------------------------------------------------
    // Storage hashing round-trip
    // -----------------------------------------------------------------------

    [Test]
    public async Task CreateApiKey_PersistsRecord_ReturnsKeyIdAndPlaintext()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);

        ApiKeyDetails result = await svc.CreateApiKey(new ApiKeyParameters {
            UserId = 7,
            Permissions = ["read", "write"]
        });

        Assert.Multiple(() => {
            Assert.That(result.Id, Is.GreaterThan(0));
            Assert.That(result.KeyId, Is.Not.Empty);
            Assert.That(result.PlaintextKey, Is.Not.Empty);
            Assert.That(result.PlaintextKey, Does.Contain("."));
            Assert.That(result.Permissions, Is.EqualTo(new[] { "read", "write" }));
            Assert.That(result.UserId, Is.EqualTo(7));
            Assert.That(result.Enabled, Is.True);
        });
    }

    [Test]
    public async Task CreateApiKey_StoredHashNotPlaintext()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);

        ApiKeyDetails result = await svc.CreateApiKey(new ApiKeyParameters { UserId = 1, Permissions = ["read"] });

        // Verify the raw DB row has hash, not the plaintext key
        ApiKey? raw = await fixture.EntityManager.Load<ApiKey>()
                                   .Where(k => k.Id == result.Id)
                                   .ExecuteEntityAsync();
        Assert.Multiple(() => {
            Assert.That(raw!.KeyHash, Is.Not.Null);
            Assert.That(raw!.KeyHash.Length, Is.EqualTo(32)); // HMAC-SHA-256 = 32 bytes
        });
    }

    [Test]
    public async Task CreateApiKey_PermissionsAreJsonEncoded()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);

        ApiKeyDetails result = await svc.CreateApiKey(new ApiKeyParameters {
            UserId = 1,
            Permissions = ["admin"]
        });

        ApiKey? raw = await fixture.EntityManager.Load<ApiKey>()
                                   .Where(k => k.Id == result.Id)
                                   .ExecuteEntityAsync();
        Assert.That(raw!.Permissions, Does.StartWith("["));
    }

    // -----------------------------------------------------------------------
    // GetApiKey — lookup verifies HMAC
    // -----------------------------------------------------------------------

    [Test]
    public async Task GetApiKey_ExistingKey_ReturnsDetails()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);

        // Create a user row so GetApiKey can join-check user.Enabled
        long userId = await fixture.EntityManager.Insert<Backend.Services.Users.User>()
                                   .Columns(u => u.Name, u => u.Enabled, u => u.CreatedAt)
                                   .Values("test", true, DateTime.UtcNow)
                                   .ReturnID()
                                   .ExecuteAsync();

        ApiKeyDetails created = await svc.CreateApiKey(new ApiKeyParameters { UserId = userId, Permissions = ["read"] });

        ApiKeyDetails fetched = await svc.GetApiKey(created.PlaintextKey!);

        Assert.That(fetched.Id, Is.EqualTo(created.Id));
    }

    [Test]
    public void GetApiKey_TamperedKey_ThrowsNotFoundException()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);

        // Tamper: change one character at the end
        Assert.ThrowsAsync<NotFoundException<ApiKey>>(
            () => svc.GetApiKey("AABBCCDD.TAMPERED999"));
    }

    [Test]
    public void GetApiKey_MissingKey_ThrowsNotFoundException()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);

        Assert.ThrowsAsync<NotFoundException<ApiKey>>(
            () => svc.GetApiKey("noDot"));
    }

    [Test]
    public void GetApiKey_EmptyString_ThrowsNotFoundException()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);

        Assert.ThrowsAsync<NotFoundException<ApiKey>>(
            () => svc.GetApiKey(""));
    }

    // -----------------------------------------------------------------------
    // HMAC fail-closed: missing or short pepper throws when Auth:Enabled=true
    // -----------------------------------------------------------------------

    [Test]
    public void Constructor_MissingPepper_AuthEnabled_Throws()
    {
        using DatabaseFixture fixture = new();
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Auth:Enabled"] = "true"
                // DIVOID_KEY_PEPPER intentionally absent
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            new ApiKeyService(fixture.EntityManager, new KeyGenerator(), config, NullLogger<ApiKeyService>.Instance));
    }

    [Test]
    public void Constructor_ShortPepper_AuthEnabled_Throws()
    {
        using DatabaseFixture fixture = new();
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["DIVOID_KEY_PEPPER"] = "short",
                ["Auth:Enabled"] = "true"
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            new ApiKeyService(fixture.EntityManager, new KeyGenerator(), config, NullLogger<ApiKeyService>.Instance));
    }

    [Test]
    public void Constructor_MissingPepper_AuthDisabled_DoesNotThrow()
    {
        using DatabaseFixture fixture = new();
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Auth:Enabled"] = "false"
                // DIVOID_KEY_PEPPER absent — should not throw when auth is disabled
            })
            .Build();

        Assert.DoesNotThrow(() =>
            new ApiKeyService(fixture.EntityManager, new KeyGenerator(), config, NullLogger<ApiKeyService>.Instance));
    }

    // -----------------------------------------------------------------------
    // Disabled key blocks lookup
    // -----------------------------------------------------------------------

    [Test]
    public async Task GetApiKey_DisabledKey_ThrowsInvalidOperation()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);

        long userId = await fixture.EntityManager.Insert<Backend.Services.Users.User>()
                                   .Columns(u => u.Name, u => u.Enabled, u => u.CreatedAt)
                                   .Values("test", true, DateTime.UtcNow)
                                   .ReturnID()
                                   .ExecuteAsync();

        ApiKeyDetails created = await svc.CreateApiKey(new ApiKeyParameters { UserId = userId, Permissions = ["read"] });

        // Disable the key
        await fixture.EntityManager.Update<ApiKey>()
                     .Set(k => k.Enabled == false)
                     .Where(k => k.Id == created.Id)
                     .ExecuteAsync();

        InvalidOperationException ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.GetApiKey(created.PlaintextKey!))!;
        Assert.That(ex.Message, Is.EqualTo("disabled_key"));
    }

    // -----------------------------------------------------------------------
    // Disabled user blocks all keys
    // -----------------------------------------------------------------------

    [Test]
    public async Task GetApiKey_DisabledUser_ThrowsInvalidOperation()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);

        long userId = await fixture.EntityManager.Insert<Backend.Services.Users.User>()
                                   .Columns(u => u.Name, u => u.Enabled, u => u.CreatedAt)
                                   .Values("disabled-user", true, DateTime.UtcNow)
                                   .ReturnID()
                                   .ExecuteAsync();

        ApiKeyDetails created = await svc.CreateApiKey(new ApiKeyParameters { UserId = userId, Permissions = ["read"] });

        // Disable the parent user
        await fixture.EntityManager.Update<Backend.Services.Users.User>()
                     .Set(u => u.Enabled == false)
                     .Where(u => u.Id == userId)
                     .ExecuteAsync();

        InvalidOperationException ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.GetApiKey(created.PlaintextKey!))!;
        Assert.That(ex.Message, Is.EqualTo("disabled_user"));
    }

    // -----------------------------------------------------------------------
    // Expired key blocks lookup
    // -----------------------------------------------------------------------

    [Test]
    public async Task GetApiKey_ExpiredKey_ThrowsInvalidOperation()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);

        long userId = await fixture.EntityManager.Insert<Backend.Services.Users.User>()
                                   .Columns(u => u.Name, u => u.Enabled, u => u.CreatedAt)
                                   .Values("test", true, DateTime.UtcNow)
                                   .ReturnID()
                                   .ExecuteAsync();

        ApiKeyDetails created = await svc.CreateApiKey(new ApiKeyParameters { UserId = userId, Permissions = ["read"] });

        // Set expiry in the past
        DateTime pastExpiry = DateTime.UtcNow.AddDays(-1);
        await fixture.EntityManager.Update<ApiKey>()
                     .Set(k => k.ExpiresAt == pastExpiry)
                     .Where(k => k.Id == created.Id)
                     .ExecuteAsync();

        InvalidOperationException ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.GetApiKey(created.PlaintextKey!))!;
        Assert.That(ex.Message, Is.EqualTo("expired"));
    }

    // -----------------------------------------------------------------------
    // GetApiKeyById
    // -----------------------------------------------------------------------

    [Test]
    public async Task GetApiKeyById_ExistingId_ReturnsDetails()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);
        ApiKeyDetails created = await svc.CreateApiKey(new ApiKeyParameters { UserId = 1, Permissions = [] });

        ApiKeyDetails fetched = await svc.GetApiKeyById(created.Id);

        Assert.That(fetched.KeyId, Is.EqualTo(created.KeyId));
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
    // UpdateApiKey / DeleteApiKey
    // -----------------------------------------------------------------------

    [Test]
    public async Task UpdateApiKey_PatchesAllowedField_ReturnsUpdatedDetails()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);
        ApiKeyDetails created = await svc.CreateApiKey(new ApiKeyParameters { UserId = 1, Permissions = [] });

        ApiKeyDetails updated = await svc.UpdateApiKey(
            created.Id,
            new PatchOperation { Op = "replace", Path = "/userid", Value = 99L });

        Assert.That(updated.UserId, Is.EqualTo(99));
    }

    [Test]
    public void UpdateApiKey_MissingId_ThrowsNotFoundException()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);

        Assert.ThrowsAsync<NotFoundException<ApiKey>>(
            () => svc.UpdateApiKey(9999, new PatchOperation { Op = "replace", Path = "/userid", Value = 1L }));
    }

    [Test]
    public async Task DeleteApiKey_ExistingKey_RemovesRecord()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);
        ApiKeyDetails created = await svc.CreateApiKey(new ApiKeyParameters { UserId = 1, Permissions = [] });

        await svc.DeleteApiKey(created.Id);

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
    // AnyAdminKeyExists
    // -----------------------------------------------------------------------

    [Test]
    public async Task AnyAdminKeyExists_NoKeys_ReturnsFalse()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);

        bool result = await svc.AnyAdminKeyExists();
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task AnyAdminKeyExists_AdminKeyPresent_ReturnsTrue()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);
        await svc.CreateApiKey(new ApiKeyParameters { UserId = 1, Permissions = ["admin"] });

        bool result = await svc.AnyAdminKeyExists();
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task AnyAdminKeyExists_OnlyReadKey_ReturnsFalse()
    {
        using DatabaseFixture fixture = new();
        ApiKeyService svc = MakeService(fixture);
        await svc.CreateApiKey(new ApiKeyParameters { UserId = 1, Permissions = ["read", "write"] });

        bool result = await svc.AnyAdminKeyExists();
        Assert.That(result, Is.False);
    }

    // -----------------------------------------------------------------------
    // ListApiKeys
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
    }
}
