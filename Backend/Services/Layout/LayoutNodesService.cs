using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Backend.Models.Nodes;
using Microsoft.Extensions.Logging;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Expressions;
using Pooshit.Ocelot.Fields;
using Pooshit.Ocelot.Tokens;

namespace Backend.Services.Layout;

/// <summary>
/// one-shot service that assigns stable 2D positions to nodes that are still at the default
/// origin (X = 0, Y = 0) using a deterministic Fruchterman-Reingold force-directed layout.
///
/// Idempotency: only nodes where both X = 0 AND Y = 0 are touched.
/// Nodes that have already been positioned (by a prior run or by user drag) are untouched.
///
/// Parameters: 50 iterations, 1000×1000 world-unit canvas, linear temperature cooling,
/// RNG seeded from the XOR of all candidate node ids (deterministic across runs).
/// </summary>
public class LayoutNodesService(IEntityManager database, ILogger<LayoutNodesService> logger)
{

    const double CanvasSize = 1000.0;
    const int Iterations = 50;
    const int ProgressInterval = 50;

    readonly IEntityManager database = database;
    readonly ILogger<LayoutNodesService> logger = logger;


    /// <summary>
    /// runs the layout pass. safe to re-run — only updates nodes still at (0, 0).
    /// </summary>
    /// <param name="ct">cancellation token</param>
    public async Task RunAsync(CancellationToken ct = default)
    {
        logger.LogInformation("event=layout.start");
        DateTimeOffset started = DateTimeOffset.UtcNow;

        // load candidate nodes: X = 0 AND Y = 0 (unpositioned)
        PredicateExpression<Node> candidatePredicate = null;
        candidatePredicate &= n => n.X == 0.0 && n.Y == 0.0;

        List<(long Id, double X, double Y)> nodes = new();

        await foreach (Node node in database.Load<Node>(n => n.Id)
                                            .Where(candidatePredicate.Content)
                                            .ExecuteEntitiesAsync())
        {
            ct.ThrowIfCancellationRequested();
            nodes.Add((node.Id, 0.0, 0.0));
        }

        int n = nodes.Count;
        logger.LogInformation("event=layout.candidates total={Total}", n);

        if (n == 0)
        {
            logger.LogInformation("event=layout.complete updated=0 elapsed=0ms reason=no_candidates");
            return;
        }

        // load all links between candidate nodes for attraction force
        long[] candidateIds = new long[n];
        for (int i = 0; i < n; i++)
            candidateIds[i] = nodes[i].Id;

        List<(int A, int B)> edges = new();
        Dictionary<long, int> idToIndex = new(n);
        for (int i = 0; i < n; i++)
            idToIndex[candidateIds[i]] = i;

        await foreach (NodeLink link in database.Load<NodeLink>(l => l.SourceId, l => l.TargetId)
                                                .Where(l => l.SourceId.In(candidateIds) && l.TargetId.In(candidateIds))
                                                .ExecuteEntitiesAsync())
        {
            ct.ThrowIfCancellationRequested();
            if (idToIndex.TryGetValue(link.SourceId, out int a) && idToIndex.TryGetValue(link.TargetId, out int b))
                edges.Add((a, b));
        }

        // deterministic seed: XOR all candidate node ids
        long seed = 0;
        foreach ((long id, _, _) in nodes)
            seed ^= id;
        Random rng = new((int)(seed & 0x7FFFFFFF));

        // initial positions: random scatter across canvas
        double[] px = new double[n];
        double[] py = new double[n];
        for (int i = 0; i < n; i++)
        {
            px[i] = rng.NextDouble() * CanvasSize;
            py[i] = rng.NextDouble() * CanvasSize;
        }

        // Fruchterman-Reingold layout
        double area = CanvasSize * CanvasSize;
        double k = Math.Sqrt(area / n);
        double tStart = Math.Sqrt(area) / 10.0;
        double tEnd = 1.0;

        double[] dx = new double[n];
        double[] dy = new double[n];

        for (int iter = 0; iter < Iterations; iter++)
        {
            ct.ThrowIfCancellationRequested();

            double t = tStart + (tEnd - tStart) * iter / (Iterations - 1);

            // reset displacement
            for (int i = 0; i < n; i++)
            {
                dx[i] = 0;
                dy[i] = 0;
            }

            // repulsion: all pairs
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    double deltax = px[i] - px[j];
                    double deltay = py[i] - py[j];
                    double dist = Math.Sqrt(deltax * deltax + deltay * deltay);
                    if (dist < 0.01) dist = 0.01;
                    double force = (k * k) / dist;
                    double fx = (deltax / dist) * force;
                    double fy = (deltay / dist) * force;
                    dx[i] += fx;
                    dy[i] += fy;
                    dx[j] -= fx;
                    dy[j] -= fy;
                }
            }

            // attraction: along edges
            foreach ((int a, int b) in edges)
            {
                double deltax = px[a] - px[b];
                double deltay = py[a] - py[b];
                double dist = Math.Sqrt(deltax * deltax + deltay * deltay);
                if (dist < 0.01) dist = 0.01;
                double force = (dist * dist) / k;
                double fx = (deltax / dist) * force;
                double fy = (deltay / dist) * force;
                dx[a] -= fx;
                dy[a] -= fy;
                dx[b] += fx;
                dy[b] += fy;
            }

            // apply displacement clamped to temperature, keep within canvas
            for (int i = 0; i < n; i++)
            {
                double dispLen = Math.Sqrt(dx[i] * dx[i] + dy[i] * dy[i]);
                if (dispLen > 0.01)
                {
                    double scale = Math.Min(dispLen, t) / dispLen;
                    px[i] += dx[i] * scale;
                    py[i] += dy[i] * scale;
                }

                px[i] = Math.Clamp(px[i], 0.0, CanvasSize);
                py[i] = Math.Clamp(py[i], 0.0, CanvasSize);
            }
        }

        // persist: update each candidate node's X and Y
        int updated = 0;
        for (int i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();

            long nodeId = nodes[i].Id;
            double newX = Math.Round(px[i], 4);
            double newY = Math.Round(py[i], 4);

            await database.Update<Node>()
                          .Set(node => node.X == newX, node => node.Y == newY)
                          .Where(node => node.Id == nodeId)
                          .ExecuteAsync();

            updated++;

            if (updated % ProgressInterval == 0)
                logger.LogInformation("event=layout.progress updated={Updated} of={Total}", updated, n);
        }

        TimeSpan elapsed = DateTimeOffset.UtcNow - started;
        logger.LogInformation(
            "event=layout.complete updated={Updated} total={Total} elapsed={Elapsed}",
            updated, n, elapsed);
    }
}
