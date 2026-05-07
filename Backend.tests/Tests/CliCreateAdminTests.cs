using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Backend.Models.Auth;
using Backend.Models.Users;
using Backend.Services.Auth;
using Backend.Services.Users;
using Backend.tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// Tests for the CLI create-admin happy path and error paths.
///
/// We test at the service layer rather than via <see cref="Backend.Cli.CliDispatcher"/> because
/// the dispatcher calls <c>Environment.Exit</c>, which would kill the test process.
/// Instead we exercise exactly the operations the dispatcher performs, verifying the end state.
/// </summary>
[TestFixture]
public class CliCreateAdminTests
{
    const string TestPepper = "cli-test-pepper-value-that-is-at-least-32-bytes-00";

    static IApiKeyService MakeApiKeyService(DatabaseFixture fixture, string? pepper = TestPepper)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["DIVOID_KEY_PEPPER"] = pepper,
                ["Auth:Enabled"] = pepper != null ? "true" : "false"
            })
            .Build();
        return new ApiKeyService(fixture.EntityManager, new KeyGenerator(), config, NullLogger<ApiKeyService>.Instance);
    }

    static IUserService MakeUserService(DatabaseFixture fixture)
        => new UserService(fixture.EntityManager);

    // -----------------------------------------------------------------------
    // Happy path — create-admin produces a working key end-to-end
    // -----------------------------------------------------------------------

    [Test]
    public async Task CreateAdmin_HappyPath_UserAndKeyCreated()
    {
        using DatabaseFixture fixture = new();
        IUserService userSvc = MakeUserService(fixture);
        IApiKeyService keySvc = MakeApiKeyService(fixture);

        UserDetails user = await userSvc.CreateUser(new UserParameters { Name = "Toni", Email = "toni@example.com" });
        ApiKeyDetails key = await keySvc.CreateApiKey(new ApiKeyParameters {
            UserId = user.Id,
            Permissions = ["admin", "write", "read"]
        });

        Assert.Multiple(() => {
            Assert.That(user.Id, Is.GreaterThan(0));
            Assert.That(user.Name, Is.EqualTo("Toni"));
            Assert.That(user.Email, Is.EqualTo("toni@example.com"));
            Assert.That(user.Enabled, Is.True);

            Assert.That(key.Id, Is.GreaterThan(0));
            Assert.That(key.PlaintextKey, Is.Not.Null.And.Not.Empty);
            Assert.That(key.PlaintextKey, Does.Contain("."));
            Assert.That(key.Permissions, Does.Contain("admin"));
        });
    }

    [Test]
    public async Task CreateAdmin_HappyPath_PlaintextKeyAuthenticates()
    {
        using DatabaseFixture fixture = new();
        IUserService userSvc = MakeUserService(fixture);
        IApiKeyService keySvc = MakeApiKeyService(fixture);

        UserDetails user = await userSvc.CreateUser(new UserParameters { Name = "Admin" });
        ApiKeyDetails created = await keySvc.CreateApiKey(new ApiKeyParameters {
            UserId = user.Id,
            Permissions = ["admin"]
        });

        // The printed key must authenticate successfully
        ApiKeyDetails fetched = await keySvc.GetApiKey(created.PlaintextKey!);

        Assert.Multiple(() => {
            Assert.That(fetched.Id, Is.EqualTo(created.Id));
            Assert.That(fetched.UserId, Is.EqualTo(user.Id));
            Assert.That(fetched.Permissions, Does.Contain("admin"));
        });
    }

    [Test]
    public async Task CreateAdmin_HappyPath_WithoutEmail_Succeeds()
    {
        using DatabaseFixture fixture = new();
        IUserService userSvc = MakeUserService(fixture);
        IApiKeyService keySvc = MakeApiKeyService(fixture);

        // email is optional
        UserDetails user = await userSvc.CreateUser(new UserParameters { Name = "NoEmail" });
        ApiKeyDetails key = await keySvc.CreateApiKey(new ApiKeyParameters {
            UserId = user.Id,
            Permissions = ["admin"]
        });

        Assert.Multiple(() => {
            Assert.That(user.Email, Is.Null.Or.Empty);
            Assert.That(key.PlaintextKey, Is.Not.Empty);
        });
    }

    // -----------------------------------------------------------------------
    // Error path — missing pepper with auth enabled
    // -----------------------------------------------------------------------

    [Test]
    public void CreateAdmin_MissingPepper_ThrowsBeforeAnyDbWork()
    {
        using DatabaseFixture fixture = new();

        // No pepper → ApiKeyService constructor throws
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Auth:Enabled"] = "true"
                // DIVOID_KEY_PEPPER absent
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            new ApiKeyService(fixture.EntityManager, new KeyGenerator(), config, NullLogger<ApiKeyService>.Instance));
    }

    // -----------------------------------------------------------------------
    // Error path — validation (name required)
    // -----------------------------------------------------------------------

    [Test]
    public void CreateAdmin_EmptyName_ServiceThrowsOrProducesNullName()
    {
        // The dispatcher validates name before calling the service.
        // Here we verify the service itself handles null gracefully (doesn't crash with NRE).
        // The dispatcher would exit(1) before this is reached in production.
        using DatabaseFixture fixture = new();
        IUserService userSvc = MakeUserService(fixture);

        // An empty name should still produce a row — name validation is the CLI's job.
        // The service doesn't enforce a non-empty name (by design — thin service layer).
        Assert.DoesNotThrowAsync(() => userSvc.CreateUser(new UserParameters { Name = "" }));
    }

    // -----------------------------------------------------------------------
    // AnyAdminHasEmail warning logic
    // -----------------------------------------------------------------------

    [Test]
    public async Task AnyAdminHasEmail_AdminWithEmail_ReturnsTrue()
    {
        using DatabaseFixture fixture = new();
        IUserService userSvc = MakeUserService(fixture);
        IApiKeyService keySvc = MakeApiKeyService(fixture);

        UserDetails user = await userSvc.CreateUser(new UserParameters { Name = "Admin", Email = "admin@example.com" });
        await keySvc.CreateApiKey(new ApiKeyParameters { UserId = user.Id, Permissions = ["admin"] });

        bool result = await userSvc.AnyAdminHasEmail();
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task AnyAdminHasEmail_AdminWithoutEmail_ReturnsFalse()
    {
        using DatabaseFixture fixture = new();
        IUserService userSvc = MakeUserService(fixture);
        IApiKeyService keySvc = MakeApiKeyService(fixture);

        UserDetails user = await userSvc.CreateUser(new UserParameters { Name = "Admin" });
        await keySvc.CreateApiKey(new ApiKeyParameters { UserId = user.Id, Permissions = ["admin"] });

        bool result = await userSvc.AnyAdminHasEmail();
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task AnyAdminHasEmail_NoKeys_ReturnsFalse()
    {
        using DatabaseFixture fixture = new();
        IUserService userSvc = MakeUserService(fixture);

        bool result = await userSvc.AnyAdminHasEmail();
        Assert.That(result, Is.False);
    }
}
