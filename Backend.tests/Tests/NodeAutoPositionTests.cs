using System;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Backend.Services.Embeddings;
using Backend.Services.Nodes;
using Backend.tests.Fixtures;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// Load-bearing tests for the auto-position inference on <c>POST /api/nodes</c> (DiVoid #311).
///
/// When a new node is created with:
///   (1) X == 0 AND Y == 0 (caller did not set a position), AND
///   (2) links is non-empty, AND
///   (3) at least one linked target has a non-zero position,
/// the server computes and persists a position for the new node.
///
/// Algorithm: pick first linked target T (in links order) with (T.X != 0 OR T.Y != 0);
///   angle = (newNodeId * 2.4) mod (2π)
///   radius = 80
///   newX = T.X + radius * cos(angle)
///   newY = T.Y + radius * sin(angle)
///
/// NEGATIVE PROOF (case 1): revert the auto-position UPDATE block in NodeService.CreateNode
/// and this test fails: the new node lands at (0, 0) instead of the computed offset.
/// POSITIVE PROOF (case 1): with the fix in place, persisted X/Y match the deterministic
/// formula exactly for the actual returned node id.
/// </summary>
[TestFixture]
public class NodeAutoPositionTests
{
    static readonly IEmbeddingCapability DisabledCapability = new EmbeddingCapability(false);

    static NodeService MakeService(DatabaseFixture fixture) => new(fixture.EntityManager, DisabledCapability);

    /// <summary>
    /// computes the expected auto-position — must stay in sync with NodeService.ComputeAutoPosition.
    /// </summary>
    static (double X, double Y) Expected(long nodeId, double anchorX, double anchorY)
    {
        const double radius = 80.0;
        double angle = (nodeId * 2.4) % (2.0 * Math.PI);
        return (anchorX + radius * Math.Cos(angle), anchorY + radius * Math.Sin(angle));
    }

    // -----------------------------------------------------------------------
    // Case 1 — Auto-position triggered (load-bearing positive + negative proof)
    // -----------------------------------------------------------------------

    /// <summary>
    /// POSITIVE PROOF: create a node linked to an existing node at (100, 200).
    /// X and Y are not set on the create request (both 0).
    /// The new node's persisted position must match the deterministic formula
    /// for the actual id that was assigned.
    ///
    /// NEGATIVE PROOF: remove the auto-position UPDATE block in NodeService.CreateNode
    /// and this assertion fails because X == 0 and Y == 0.
    /// </summary>
    [Test]
    public async Task CreateNode_WithLinkedPositionedTarget_AutoPositions()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        // create anchor node and give it an explicit position via PATCH
        NodeDetails anchor = await svc.CreateNode(new NodeDetails { Type = "doc", Name = "Anchor" });
        await svc.Patch(anchor.Id,
            new Pooshit.AspNetCore.Services.Patches.PatchOperation { Op = "replace", Path = "/X", Value = 100.0 },
            new Pooshit.AspNetCore.Services.Patches.PatchOperation { Op = "replace", Path = "/Y", Value = 200.0 });

        // create new node linked to the anchor, without setting X/Y
        NodeDetails created = await svc.CreateNode(new NodeDetails {
            Type = "task",
            Name = "AutoPositioned",
            Links = [anchor.Id]
        });

        // re-read to confirm the position was persisted, not just echoed
        NodeDetails fetched = await svc.GetNodeById(created.Id);

        (double expectedX, double expectedY) = Expected(fetched.Id, 100.0, 200.0);

        Assert.That(fetched.X, Is.Not.Null, "X must be present in GET response");
        Assert.That(fetched.Y, Is.Not.Null, "Y must be present in GET response");
        Assert.That(fetched.X.Value, Is.EqualTo(expectedX).Within(1e-9),
            $"X must match golden-angle formula for nodeId={fetched.Id}, anchor=(100,200)");
        Assert.That(fetched.Y.Value, Is.EqualTo(expectedY).Within(1e-9),
            $"Y must match golden-angle formula for nodeId={fetched.Id}, anchor=(100,200)");
        Assert.That(fetched.X.Value, Is.Not.EqualTo(0.0), "auto-positioned X must not be 0");
        Assert.That(fetched.Y.Value, Is.Not.EqualTo(0.0), "auto-positioned Y must not be 0");
    }

    // -----------------------------------------------------------------------
    // Case 2 — Caller-explicit position respected
    // -----------------------------------------------------------------------

    /// <summary>
    /// When the caller explicitly supplies X and Y (non-zero), those values must be
    /// persisted as-is even when a positioned link target exists.
    /// Auto-position must not overwrite an explicit placement.
    /// </summary>
    [Test]
    public async Task CreateNode_WithExplicitPosition_RespectsCaller()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails anchor = await svc.CreateNode(new NodeDetails { Type = "doc", Name = "Anchor" });
        await svc.Patch(anchor.Id,
            new Pooshit.AspNetCore.Services.Patches.PatchOperation { Op = "replace", Path = "/X", Value = 500.0 },
            new Pooshit.AspNetCore.Services.Patches.PatchOperation { Op = "replace", Path = "/Y", Value = 600.0 });

        NodeDetails created = await svc.CreateNode(new NodeDetails {
            Type = "task",
            Name = "ExplicitPosition",
            X = 42.5,
            Y = 99.0,
            Links = [anchor.Id]
        });

        NodeDetails fetched = await svc.GetNodeById(created.Id);

        Assert.That(fetched.X, Is.Not.Null);
        Assert.That(fetched.Y, Is.Not.Null);
        Assert.That(fetched.X.Value, Is.EqualTo(42.5).Within(1e-9),
            "explicit X=42.5 must survive create and not be overwritten by auto-position");
        Assert.That(fetched.Y.Value, Is.EqualTo(99.0).Within(1e-9),
            "explicit Y=99.0 must survive create and not be overwritten by auto-position");
    }

    // -----------------------------------------------------------------------
    // Case 3 — No qualifying target (all linked targets at origin)
    // -----------------------------------------------------------------------

    /// <summary>
    /// When all linked targets are at (0, 0), there is no valid anchor.
    /// The new node must stay at (0, 0) — no auto-position applied.
    /// </summary>
    [Test]
    public async Task CreateNode_LinkedToOriginTargets_StaysAtOrigin()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        // target has no explicit position — stays at DB default (0, 0)
        NodeDetails target = await svc.CreateNode(new NodeDetails { Type = "doc", Name = "OriginTarget" });

        NodeDetails created = await svc.CreateNode(new NodeDetails {
            Type = "task",
            Name = "NoAnchor",
            Links = [target.Id]
        });

        NodeDetails fetched = await svc.GetNodeById(created.Id);

        // X/Y may be null (not in default list fields) or 0 — both are acceptable as "at origin"
        double x = fetched.X ?? 0.0;
        double y = fetched.Y ?? 0.0;
        Assert.That(x, Is.EqualTo(0.0),
            "new node must stay at X=0 when all linked targets are at origin");
        Assert.That(y, Is.EqualTo(0.0),
            "new node must stay at Y=0 when all linked targets are at origin");
    }

    // -----------------------------------------------------------------------
    // Case 4 — No links at all
    // -----------------------------------------------------------------------

    /// <summary>
    /// When no links are provided, auto-position must not trigger.
    /// New node stays at (0, 0).
    /// </summary>
    [Test]
    public async Task CreateNode_WithoutLinks_StaysAtOrigin()
    {
        using DatabaseFixture fixture = new();
        NodeService svc = MakeService(fixture);

        NodeDetails created = await svc.CreateNode(new NodeDetails {
            Type = "task",
            Name = "NoLinks"
        });

        NodeDetails fetched = await svc.GetNodeById(created.Id);

        double x = fetched.X ?? 0.0;
        double y = fetched.Y ?? 0.0;
        Assert.That(x, Is.EqualTo(0.0), "new node without links must stay at X=0");
        Assert.That(y, Is.EqualTo(0.0), "new node without links must stay at Y=0");
    }
}
