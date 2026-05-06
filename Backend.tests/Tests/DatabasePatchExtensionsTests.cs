using Backend.Extensions;
using Backend.Models.Auth;
using Backend.tests.Fixtures;
using Pooshit.AspNetCore.Services.Errors.Exceptions;
using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Ocelot.Clients;

namespace Backend.tests.Tests;

/// <summary>
/// Tests for <see cref="DatabasePatchExtensions.Patch{T}"/>.
///
/// We exercise the extension through the full database round-trip because the method builds
/// Ocelot expression trees that are not meaningful without real SQL execution.
///
/// <see cref="ApiKey"/> is used as the patch target — its <c>UserId</c> (long) and
/// <c>Permissions</c> (string) properties are both tagged <c>[AllowPatch]</c>.
/// </summary>
[TestFixture]
public class DatabasePatchExtensionsTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>Inserts a seed ApiKey row and returns its auto-assigned id.</summary>
    static async Task<long> InsertApiKey(DatabaseFixture fixture, long userId = 1, string permissions = "[]")
    {
        return await fixture.EntityManager.Insert<ApiKey>()
                            .Columns(k => k.Key, k => k.UserId, k => k.Permissions)
                            .Values("test-key-" + Guid.NewGuid().ToString("N"), userId, permissions)
                            .ReturnID()
                            .ExecuteAsync();
    }

    static async Task<ApiKey?> LoadKey(DatabaseFixture fixture, long id)
    {
        return await fixture.EntityManager.Load<ApiKey>()
                            .Where(k => k.Id == id)
                            .ExecuteEntityAsync();
    }

    // ------------------------------------------------------------------
    // replace
    // ------------------------------------------------------------------

    [Test]
    public async Task Patch_Replace_UserId_UpdatesValue()
    {
        using DatabaseFixture fixture = new();
        long id = await InsertApiKey(fixture, userId: 1);

        await fixture.EntityManager.Update<ApiKey>()
                     .Patch(new PatchOperation { Op = "replace", Path = "/userid", Value = 42L })
                     .Where(k => k.Id == id)
                     .ExecuteAsync();

        ApiKey? updated = await LoadKey(fixture, id);
        Assert.That(updated!.UserId, Is.EqualTo(42));
    }

    [Test]
    public async Task Patch_Replace_Permissions_UpdatesValue()
    {
        using DatabaseFixture fixture = new();
        long id = await InsertApiKey(fixture, permissions: "[\"read\"]");

        await fixture.EntityManager.Update<ApiKey>()
                     .Patch(new PatchOperation { Op = "replace", Path = "/permissions", Value = "[\"admin\"]" })
                     .Where(k => k.Id == id)
                     .ExecuteAsync();

        ApiKey? updated = await LoadKey(fixture, id);
        Assert.That(updated!.Permissions, Is.EqualTo("[\"admin\"]"));
    }

    // ------------------------------------------------------------------
    // add / remove (numeric increment / decrement)
    // ------------------------------------------------------------------

    [Test]
    public async Task Patch_Add_UserId_IncrementsValue()
    {
        using DatabaseFixture fixture = new();
        long id = await InsertApiKey(fixture, userId: 10);

        await fixture.EntityManager.Update<ApiKey>()
                     .Patch(new PatchOperation { Op = "add", Path = "/userid", Value = 5L })
                     .Where(k => k.Id == id)
                     .ExecuteAsync();

        ApiKey? updated = await LoadKey(fixture, id);
        Assert.That(updated!.UserId, Is.EqualTo(15));
    }

    [Test]
    public async Task Patch_Remove_UserId_DecrementsValue()
    {
        using DatabaseFixture fixture = new();
        long id = await InsertApiKey(fixture, userId: 10);

        await fixture.EntityManager.Update<ApiKey>()
                     .Patch(new PatchOperation { Op = "remove", Path = "/userid", Value = 3L })
                     .Where(k => k.Id == id)
                     .ExecuteAsync();

        ApiKey? updated = await LoadKey(fixture, id);
        Assert.That(updated!.UserId, Is.EqualTo(7));
    }

    // ------------------------------------------------------------------
    // flag / unflag (bitwise OR / AND-NOT) — UserId is a long, suitable target
    // ------------------------------------------------------------------

    [Test]
    public async Task Patch_Flag_UserId_SetsFlag()
    {
        using DatabaseFixture fixture = new();
        long id = await InsertApiKey(fixture, userId: 0b0001);

        await fixture.EntityManager.Update<ApiKey>()
                     .Patch(new PatchOperation { Op = "flag", Path = "/userid", Value = 0b0010L })
                     .Where(k => k.Id == id)
                     .ExecuteAsync();

        ApiKey? updated = await LoadKey(fixture, id);
        Assert.That(updated!.UserId, Is.EqualTo(0b0011));
    }

    [Test]
    public async Task Patch_Unflag_UserId_ClearsFlag()
    {
        using DatabaseFixture fixture = new();
        long id = await InsertApiKey(fixture, userId: 0b0111);

        await fixture.EntityManager.Update<ApiKey>()
                     .Patch(new PatchOperation { Op = "unflag", Path = "/userid", Value = 0b0010L })
                     .Where(k => k.Id == id)
                     .ExecuteAsync();

        ApiKey? updated = await LoadKey(fixture, id);
        Assert.That(updated!.UserId, Is.EqualTo(0b0101));
    }

    // ------------------------------------------------------------------
    // Error cases
    // ------------------------------------------------------------------

    [Test]
    public void Patch_UnknownPath_ThrowsPropertyNotFoundException()
    {
        using DatabaseFixture fixture = new();
        Assert.Throws<PropertyNotFoundException>(() =>
            fixture.EntityManager.Update<ApiKey>()
                   .Patch(new PatchOperation { Op = "replace", Path = "/nonexistentfield", Value = "x" })
        );
    }

    [Test]
    public void Patch_NonAllowPatchProperty_ThrowsNotSupportedException()
    {
        // ApiKey.Key is not tagged [AllowPatch], so patching it must throw.
        using DatabaseFixture fixture = new();
        Assert.Throws<NotSupportedException>(() =>
            fixture.EntityManager.Update<ApiKey>()
                   .Patch(new PatchOperation { Op = "replace", Path = "/key", Value = "new-key" })
        );
    }

    [Test]
    public void Patch_UnsupportedOp_ThrowsArgumentException()
    {
        using DatabaseFixture fixture = new();
        Assert.Throws<ArgumentException>(() =>
            fixture.EntityManager.Update<ApiKey>()
                   .Patch(new PatchOperation { Op = "copy", Path = "/userid", Value = 1L })
        );
    }

    // ------------------------------------------------------------------
    // Multiple operations in one call
    // ------------------------------------------------------------------

    [Test]
    public async Task Patch_MultipleOps_AppliesAll()
    {
        using DatabaseFixture fixture = new();
        long id = await InsertApiKey(fixture, userId: 1, permissions: "[\"read\"]");

        await fixture.EntityManager.Update<ApiKey>()
                     .Patch(
                         new PatchOperation { Op = "replace", Path = "/userid", Value = 99L },
                         new PatchOperation { Op = "replace", Path = "/permissions", Value = "[\"write\"]" })
                     .Where(k => k.Id == id)
                     .ExecuteAsync();

        ApiKey? updated = await LoadKey(fixture, id);
        Assert.Multiple(() => {
            Assert.That(updated!.UserId, Is.EqualTo(99));
            Assert.That(updated!.Permissions, Is.EqualTo("[\"write\"]"));
        });
    }
}
