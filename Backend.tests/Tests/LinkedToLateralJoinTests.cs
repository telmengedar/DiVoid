using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Backend.Init;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Npgsql;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Info;

namespace Backend.tests.Tests;

/// <summary>
/// Postgres-gated integration tests for the LATERAL JOIN path in
/// <see cref="NodeService.BuildLinkedToFilter"/> (task #572, Ocelot v0.22.0-preview).
///
/// All tests in this class are gated on the <c>POSTGRES_CONNECTION</c>
/// environment variable and call <see cref="Assert.Inconclusive"/> when it
/// is absent so the rest of the suite continues normally.
///
/// These tests validate that:
/// 1. the LATERAL branch (Postgres) returns the correct neighbours and excludes
///    the seed set.
/// 2. forcing <c>SupportsLateralJoin = false</c> on a real Postgres connection
///    still produces the correct result via the UNION-based fallback branch.
/// </summary>
[TestFixture]
public class LinkedToLateralJoinTests
{
    static readonly IEmbeddingCapability DisabledCapability = new EmbeddingCapability(false);

    static IEntityManager CreatePostgresManager(string connString)
    {
        IDBClient client = ClientFactory.Create(() => new NpgsqlConnection(connString), new PostgreInfo(), true);
        return new EntityManager(client);
    }

    static async Task ApplySchema(IEntityManager em)
    {
        DatabaseModelService svc = new(em);
        await svc.StartAsync(CancellationToken.None);
    }

    static NodeService MakeService(IEntityManager em) => new(em, DisabledCapability);

    static async Task<NodeDetails> Create(NodeService svc, string name)
        => await svc.CreateNode(new NodeDetails { Type = "task", Name = name }, callerId: 0);

    static async Task<List<NodeDetails>> CollectPage(AsyncPageResponseWriter<NodeDetails> writer)
    {
        byte[] buffer;
        using (MemoryStream ms = new())
        {
            await writer.Write(ms);
            buffer = ms.ToArray();
        }
        using MemoryStream readStream = new(buffer);
        string json = await new StreamReader(readStream).ReadToEndAsync();
        Pooshit.AspNetCore.Services.Data.Page<NodeDetails> page =
            Pooshit.Json.Json.Read<Pooshit.AspNetCore.Services.Data.Page<NodeDetails>>(json);
        return page.Result?.ToList() ?? [];
    }

    static void PurgeTestData(IEntityManager em)
    {
        em.Delete<NodeLink>().Execute();
        em.Delete<Node>().Execute();
    }

    [Test]
    public async Task LinkedTo_LateralBranch_Postgres_FindsNeighbourExcludesSeed()
    {
        string? connString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION");
        if (string.IsNullOrEmpty(connString))
            Assert.Inconclusive("POSTGRES_CONNECTION not set — Postgres LATERAL JOIN tests skipped");

        IEntityManager em = CreatePostgresManager(connString);
        await ApplySchema(em);
        PurgeTestData(em);

        NodeService svc = MakeService(em);

        NodeDetails a = await Create(svc, "NodeA");
        NodeDetails b = await Create(svc, "NodeB");
        NodeDetails c = await Create(svc, "NodeC");

        await svc.LinkNodes(a.Id, b.Id, callerId: 0, isAdmin: true);

        AsyncPageResponseWriter<NodeDetails> writer = await svc.ListPaged(
            new NodeFilter { LinkedTo = [a.Id], Count = 100 }, callerId: 0, isAdmin: true);
        List<NodeDetails> results = await CollectPage(writer);

        long[] ids = results.Select(n => n.Id).ToArray();
        Assert.Multiple(() => {
            Assert.That(em.DBClient.DBInfo.SupportsLateralJoin, Is.True,
                "Postgres must report SupportsLateralJoin = true so the LATERAL branch fired");
            Assert.That(ids, Does.Contain(b.Id),
                "B is linked to A — must appear when LinkedTo=A");
            Assert.That(ids, Does.Not.Contain(a.Id),
                "A is the seed — must be excluded from results");
            Assert.That(ids, Does.Not.Contain(c.Id),
                "C has no link to A — must not appear");
        });
    }

    [Test]
    public async Task LinkedTo_ForcedFallback_Postgres_SameResultAsLateralBranch()
    {
        string? connString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION");
        if (string.IsNullOrEmpty(connString))
            Assert.Inconclusive("POSTGRES_CONNECTION not set — Postgres LATERAL JOIN tests skipped");

        IEntityManager pgEm = CreatePostgresManager(connString);
        await ApplySchema(pgEm);
        PurgeTestData(pgEm);

        NodeService pgSvc = MakeService(pgEm);

        NodeDetails a = await Create(pgSvc, "NodeA");
        NodeDetails b = await Create(pgSvc, "NodeB");
        NodeDetails c = await Create(pgSvc, "NodeC");

        await pgSvc.LinkNodes(a.Id, b.Id, callerId: 0, isAdmin: true);

        IEntityManager fallbackEm = LateralCapabilityForcedFalseProxy.Wrap(pgEm);
        NodeService fallbackSvc = MakeService(fallbackEm);

        AsyncPageResponseWriter<NodeDetails> writer = await fallbackSvc.ListPaged(
            new NodeFilter { LinkedTo = [a.Id], Count = 100 }, callerId: 0, isAdmin: true);
        List<NodeDetails> results = await CollectPage(writer);

        long[] ids = results.Select(n => n.Id).ToArray();
        Assert.Multiple(() => {
            Assert.That(fallbackEm.DBClient.DBInfo.SupportsLateralJoin, Is.False,
                "Forced-fallback must report SupportsLateralJoin = false (UNION path taken)");
            Assert.That(ids, Does.Contain(b.Id),
                "Fallback branch: B is linked to A — must appear when LinkedTo=A");
            Assert.That(ids, Does.Not.Contain(a.Id),
                "Fallback branch: A is the seed — must be excluded");
            Assert.That(ids, Does.Not.Contain(c.Id),
                "Fallback branch: C has no link to A — must not appear");
        });
    }
}

/// <summary>
/// <see cref="DispatchProxy"/>-based wrapper over an <see cref="IEntityManager"/> that
/// replaces <see cref="IDBInfo.SupportsLateralJoin"/> with <c>false</c>, forcing
/// <see cref="NodeService"/> to take the UNION-based fallback path even when the
/// underlying connection is Postgres.
/// </summary>
internal class LateralCapabilityForcedFalseProxy : DispatchProxy
{
    IEntityManager? inner;
    IDBClient? wrappedClient;

    public static IEntityManager Wrap(IEntityManager real)
    {
        IEntityManager proxy = Create<IEntityManager, LateralCapabilityForcedFalseProxy>();
        LateralCapabilityForcedFalseProxy p = (LateralCapabilityForcedFalseProxy)(object)proxy;
        p.inner = real;

        IDBClient clientProxy = LateralCapabilityDisabledClientProxy.Wrap(real.DBClient);
        p.wrappedClient = clientProxy;

        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null)
            return null;

        if (targetMethod.Name == "get_" + nameof(IEntityManager.DBClient))
            return wrappedClient;

        return targetMethod.Invoke(inner, args);
    }
}

internal class LateralCapabilityDisabledClientProxy : DispatchProxy
{
    IDBClient? inner;
    IDBInfo? overrideInfo;

    public static IDBClient Wrap(IDBClient real)
    {
        IDBClient proxy = Create<IDBClient, LateralCapabilityDisabledClientProxy>();
        LateralCapabilityDisabledClientProxy p = (LateralCapabilityDisabledClientProxy)(object)proxy;
        p.inner = real;
        p.overrideInfo = LateralCapabilityDisabledInfo.Wrap(real.DBInfo);
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null)
            return null;

        if (targetMethod.Name == "get_" + nameof(IDBClient.DBInfo))
            return overrideInfo;

        return targetMethod.Invoke(inner, args);
    }
}

internal class LateralCapabilityDisabledInfo : DispatchProxy
{
    IDBInfo? inner;

    public static IDBInfo Wrap(IDBInfo real)
    {
        IDBInfo proxy = Create<IDBInfo, LateralCapabilityDisabledInfo>();
        LateralCapabilityDisabledInfo p = (LateralCapabilityDisabledInfo)(object)proxy;
        p.inner = real;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null)
            return null;

        if (targetMethod.Name == "get_" + nameof(IDBInfo.SupportsLateralJoin))
            return false;

        return targetMethod.Invoke(inner, args);
    }
}
