using Backend.Init;
using Backend.tests.Fixtures;
using Backend.Models.Auth;
using Backend.Models.Users;
using Backend.Services.Auth;
using Backend.Services.Users;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Info;

namespace Backend.tests.Tests;

/// <summary>
/// Tests that <see cref="IApiKeyService.GetApiKey"/> finishes its LastUsedAt write before it returns.
/// </summary>
[TestFixture, Parallelizable]
public class ApiKeyLastUsedWriteTests
{
    const string TestPepper = "last-used-test-pepper-value-at-least-32-bytes-00";

    [Test]
    [Description("DiVoid #16165: a LastUsedAt write still running after GetApiKey returns races the disposal of the owner's connection (Collection was modified in SqliteConnection.Dispose).")]
    [Parallelizable]
    public async Task GetApiKey_WhileLastUsedWriteIsPending_DoesNotComplete()
    {
        using SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        IDBClient client = UpdateGateClient.Wrap(ClientFactory.Create(connection, new SQLiteInfo()), out UpdateGateClient gate);
        IEntityManager entityManager = new EntityManager(client);
        await new DatabaseModelService(entityManager).StartAsync(CancellationToken.None);

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["DIVOID_KEY_PEPPER"] = TestPepper,
                ["Auth:Enabled"] = "true"
            })
            .Build();
        IApiKeyService keySvc = new ApiKeyService(entityManager, new KeyGenerator(), config, NullLogger<ApiKeyService>.Instance);
        UserDetails user = await new UserService(entityManager).CreateUser(new UserParameters { Name = "U" });
        ApiKeyDetails created = await keySvc.CreateApiKey(new ApiKeyParameters { UserId = user.Id, Permissions = ["read"] });

        gate.Arm();
        Task<ApiKeyDetails> authentication = keySvc.GetApiKey(created.PlaintextKey!);
        try {
            Task first;
            try {
                first = await Task.WhenAny(gate.UpdateStarted, authentication).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException) {
                Assert.Fail("LastUsedAt UPDATE never reached the gate");
                return;
            }

            Assert.That(first, Is.SameAs(gate.UpdateStarted), "GetApiKey returned before its LastUsedAt UPDATE even started");
            Assert.That(authentication.IsCompleted, Is.False, "GetApiKey returned while its LastUsedAt write was still pending");
        }
        finally {
            gate.Release();
        }

        await authentication;
        ApiKeyDetails reloaded = await keySvc.GetApiKeyById(created.Id);
        Assert.That(reloaded.LastUsedAt, Is.Not.Null);
    }
}
