using System;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Patches;

namespace Backend.tests.Tests;

/// <summary>
/// Load-bearing tests for the auto-position inference on <c>POST /api/nodes/{id}/links</c> (DiVoid #500).
///
/// When two nodes are linked and exactly one endpoint is at (0, 0):
///   the (0, 0) endpoint is moved to ComputeAutoPosition(endpointId, anchor.X, anchor.Y).
///
/// Algorithm (same as CreateNode path, shared helper TryAnchorOrphanToPositioned):
///   angle = (nodeId * 2.4) mod (2π)
///   radius = 200
///   newX = anchor.X + radius * cos(angle)
///   newY = anchor.Y + radius * sin(angle)
///
/// NEGATIVE PROOF (T1/T2): remove the TryAnchorOrphanToPositioned calls from LinkNodes
/// and these tests fail: the (0, 0) node stays at origin instead of moving.
/// POSITIVE PROOF (T1/T2): with the helper calls in place, persisted X/Y match
/// the deterministic formula exactly.
///
/// NEGATIVE PROOF (T5, radius-lock): revert the radius constant in ComputeAutoPosition
/// from 200.0 to 80.0 and T5 fails: the measured distance from anchor is 80, not 200.
/// </summary>
[TestFixture]
public class NodeLinkAutoPositionTests
{
    static readonly IEmbeddingCapability DisabledCapability = new EmbeddingCapability(false);

    static NodeService MakeService(DatabaseFixture fixture) => new(fixture.EntityManager, DisabledCapability);

    /// <summary>
    /// computes the expected auto-position — must stay in sync with NodeService.ComputeAutoPosition.
    /// </summary>
    static (double X, double Y) Expected(long nodeId, double anchorX, double anchorY)
    {
        const double radius = 200.0;
        double angle = (nodeId * 2.4) % (2.0 * Math.PI);
        return (anchorX + radius * Math.Cos(angle), anchorY + radius * Math.Sin(angle));
    }

    static async Task<NodeDetails> CreatePositionedNode(NodeService svc, string name, double x, double y)
    {
        NodeDetails node = await svc.CreateNode(new NodeDetails { Type = "doc", Name = name });
        await svc.Patch(node.Id,
            [
                new PatchOperation { Op = "replace", Path = "/X", Value = x },
                new PatchOperation { Op = "replace", Path = "/Y", Value = y }
            ],
            CancellationToken.None);
        return await svc.GetNodeById(node.Id);
    }

    /// <summary>
    /// T1 — POSITIVE PROOF: source at (0, 0), target at (100, 200).
    /// After LinkNodes(sourceId, targetId), source must move to ComputeAutoPosition(sourceId, 100, 200).
    /// Target must stay at (100, 200).
    ///
    /// NEGATIVE PROOF: remove the first TryAnchorOrphanToPositioned call from LinkNodes;
    /// source stays at (0, 0) and the assertion fails.
    /// </summary>
    [Test]
    public async Task LinkNodes_SourceAtOrigin_MovesSourceTowardPositionedTarget()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails source = await svc.CreateNode(new NodeDetails { Type = "doc", Name = "Source" });
        NodeDetails target = await CreatePositionedNode(svc, "Target", 100.0, 200.0);

        await svc.LinkNodes(source.Id, target.Id);

        NodeDetails fetchedSource = await svc.GetNodeById(source.Id);
        NodeDetails fetchedTarget = await svc.GetNodeById(target.Id);

        (double expectedX, double expectedY) = Expected(source.Id, 100.0, 200.0);

        Assert.That(fetchedSource.X, Is.Not.Null, "source X must be present after linking");
        Assert.That(fetchedSource.Y, Is.Not.Null, "source Y must be present after linking");
        Assert.That(fetchedSource.X.Value, Is.EqualTo(expectedX).Within(1e-9),
            $"source X must match golden-angle formula for nodeId={source.Id}, anchor=(100,200)");
        Assert.That(fetchedSource.Y.Value, Is.EqualTo(expectedY).Within(1e-9),
            $"source Y must match golden-angle formula for nodeId={source.Id}, anchor=(100,200)");
        Assert.That(fetchedSource.X.Value, Is.Not.EqualTo(0.0), "auto-positioned source X must not be 0");

        Assert.That(fetchedTarget.X, Is.Not.Null, "target X must be present after linking");
        Assert.That(fetchedTarget.Y, Is.Not.Null, "target Y must be present after linking");
        Assert.That(fetchedTarget.X.Value, Is.EqualTo(100.0).Within(1e-9),
            "target X must remain unchanged at 100.0");
        Assert.That(fetchedTarget.Y.Value, Is.EqualTo(200.0).Within(1e-9),
            "target Y must remain unchanged at 200.0");
    }

    /// <summary>
    /// T2 — POSITIVE PROOF: source at (100, 200), target at (0, 0).
    /// After LinkNodes(sourceId, targetId), target must move to ComputeAutoPosition(targetId, 100, 200).
    /// Source must stay at (100, 200).
    ///
    /// NEGATIVE PROOF: remove the second TryAnchorOrphanToPositioned call from LinkNodes;
    /// target stays at (0, 0) and the assertion fails.
    /// </summary>
    [Test]
    public async Task LinkNodes_TargetAtOrigin_MovesTargetTowardPositionedSource()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails source = await CreatePositionedNode(svc, "Source", 100.0, 200.0);
        NodeDetails target = await svc.CreateNode(new NodeDetails { Type = "doc", Name = "Target" });

        await svc.LinkNodes(source.Id, target.Id);

        NodeDetails fetchedSource = await svc.GetNodeById(source.Id);
        NodeDetails fetchedTarget = await svc.GetNodeById(target.Id);

        (double expectedX, double expectedY) = Expected(target.Id, 100.0, 200.0);

        Assert.That(fetchedTarget.X, Is.Not.Null, "target X must be present after linking");
        Assert.That(fetchedTarget.Y, Is.Not.Null, "target Y must be present after linking");
        Assert.That(fetchedTarget.X.Value, Is.EqualTo(expectedX).Within(1e-9),
            $"target X must match golden-angle formula for nodeId={target.Id}, anchor=(100,200)");
        Assert.That(fetchedTarget.Y.Value, Is.EqualTo(expectedY).Within(1e-9),
            $"target Y must match golden-angle formula for nodeId={target.Id}, anchor=(100,200)");
        Assert.That(fetchedTarget.X.Value, Is.Not.EqualTo(0.0), "auto-positioned target X must not be 0");

        Assert.That(fetchedSource.X, Is.Not.Null, "source X must be present after linking");
        Assert.That(fetchedSource.Y, Is.Not.Null, "source Y must be present after linking");
        Assert.That(fetchedSource.X.Value, Is.EqualTo(100.0).Within(1e-9),
            "source X must remain unchanged at 100.0");
        Assert.That(fetchedSource.Y.Value, Is.EqualTo(200.0).Within(1e-9),
            "source Y must remain unchanged at 200.0");
    }

    /// <summary>
    /// T3 — both endpoints already positioned; neither must move.
    /// </summary>
    [Test]
    public async Task LinkNodes_BothPositioned_NeitherMoves()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails source = await CreatePositionedNode(svc, "Source", 10.0, 20.0);
        NodeDetails target = await CreatePositionedNode(svc, "Target", 100.0, 200.0);

        await svc.LinkNodes(source.Id, target.Id);

        NodeDetails fetchedSource = await svc.GetNodeById(source.Id);
        NodeDetails fetchedTarget = await svc.GetNodeById(target.Id);

        Assert.That(fetchedSource.X, Is.Not.Null, "source X must be present");
        Assert.That(fetchedSource.Y, Is.Not.Null, "source Y must be present");
        Assert.That(fetchedSource.X.Value, Is.EqualTo(10.0).Within(1e-9),
            "source X must remain at explicit 10.0 when both endpoints are positioned");
        Assert.That(fetchedSource.Y.Value, Is.EqualTo(20.0).Within(1e-9),
            "source Y must remain at explicit 20.0 when both endpoints are positioned");
        Assert.That(fetchedTarget.X, Is.Not.Null, "target X must be present");
        Assert.That(fetchedTarget.Y, Is.Not.Null, "target Y must be present");
        Assert.That(fetchedTarget.X.Value, Is.EqualTo(100.0).Within(1e-9),
            "target X must remain at explicit 100.0 when both endpoints are positioned");
        Assert.That(fetchedTarget.Y.Value, Is.EqualTo(200.0).Within(1e-9),
            "target Y must remain at explicit 200.0 when both endpoints are positioned");
    }

    /// <summary>
    /// T4 — both endpoints at (0, 0); no anchor exists, neither must move.
    /// The link is still inserted.
    /// </summary>
    [Test]
    public async Task LinkNodes_BothAtOrigin_NeitherMoves()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails source = await svc.CreateNode(new NodeDetails { Type = "doc", Name = "Source" });
        NodeDetails target = await svc.CreateNode(new NodeDetails { Type = "doc", Name = "Target" });

        await svc.LinkNodes(source.Id, target.Id);

        NodeDetails fetchedSource = await svc.GetNodeById(source.Id);
        NodeDetails fetchedTarget = await svc.GetNodeById(target.Id);

        double sourceX = fetchedSource.X ?? 0.0;
        double sourceY = fetchedSource.Y ?? 0.0;
        double targetX = fetchedTarget.X ?? 0.0;
        double targetY = fetchedTarget.Y ?? 0.0;

        Assert.That(sourceX, Is.EqualTo(0.0),
            "source must stay at X=0 when both endpoints are at origin");
        Assert.That(sourceY, Is.EqualTo(0.0),
            "source must stay at Y=0 when both endpoints are at origin");
        Assert.That(targetX, Is.EqualTo(0.0),
            "target must stay at X=0 when both endpoints are at origin");
        Assert.That(targetY, Is.EqualTo(0.0),
            "target must stay at Y=0 when both endpoints are at origin");
    }

    /// <summary>
    /// T5 — radius-lock: after linking a (0, 0) node to a positioned anchor,
    /// the resulting position must be exactly 200.0 units from the anchor.
    ///
    /// NEGATIVE PROOF: revert the radius constant in NodeService.ComputeAutoPosition
    /// from 200.0 to 80.0; the measured distance becomes 80.0 and this assertion fails.
    /// </summary>
    [Test]
    public async Task LinkNodes_MovedNode_IsExactly200UnitsFromAnchor()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails source = await svc.CreateNode(new NodeDetails { Type = "doc", Name = "Source" });
        NodeDetails target = await CreatePositionedNode(svc, "Target", 300.0, 400.0);

        await svc.LinkNodes(source.Id, target.Id);

        NodeDetails fetchedSource = await svc.GetNodeById(source.Id);

        Assert.That(fetchedSource.X, Is.Not.Null, "source X must be set after linking to positioned target");
        Assert.That(fetchedSource.Y, Is.Not.Null, "source Y must be set after linking to positioned target");

        double dx = fetchedSource.X.Value - 300.0;
        double dy = fetchedSource.Y.Value - 400.0;
        double distance = Math.Sqrt(dx * dx + dy * dy);

        Assert.That(distance, Is.EqualTo(200.0).Within(1e-9),
            $"moved node must be exactly 200.0 units from anchor (300,400); actual distance={distance}");
    }
}
