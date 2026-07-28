/**
 * Load-bearing tests for direction + context edge metadata (DiVoid #7142 PR2 / #275).
 *
 * Covers the three pure functions WorkspaceCanvas composes to turn inline
 * `linkDetails` (backend #167/#7156) into directed, labelled xyflow edges:
 *
 *  - collectLinkDetails: builds the canonical-id → NodeLink lookup.
 *  - markersForLinkType: linkType → markerStart/markerEnd config.
 *  - joinLinkMetadata: normalizes edge source/target to the true NodeLink
 *    orientation and attaches data + markers.
 *
 * Each test documents the mental-deletion that would make it fail, per the
 * load-bearing discipline (rule #275 / FE contract §13.1).
 */

import { describe, it, expect } from 'vitest';
import { MarkerType } from '@xyflow/react';
import type { NodeLink, PositionedNodeDetails } from '@/types/divoid';

describe('collectLinkDetails', () => {
  /**
   * POSITIVE PROOF: a link present on both endpoints' linkDetails rows is
   * collected once, keyed by the canonical `${lo}-${hi}` id.
   *
   * Mental-deletion: remove the `if (byId.has(key)) continue` dedup guard —
   * this test would still pass (second write is identical), so the REAL
   * mental-deletion for dedup is proven by the id-key assertion itself: if
   * the key were built from iteration order instead of `Math.min/max(source,
   * target)`, the second (identical) row would collide under a DIFFERENT key
   * and `byId.size` would be 2 instead of 1.
   */
  it('collects a link visible on both endpoints under one canonical key', async () => {
    const { collectLinkDetails } = await import('./WorkspaceCanvas');

    const link: NodeLink = { sourceId: 10, targetId: 20, linkType: 'Unidirectional', context: 'blocks' };
    const nodes: PositionedNodeDetails[] = [
      { id: 10, type: 'task', name: 'A', status: null, x: 0, y: 0, linkDetails: [link] },
      { id: 20, type: 'task', name: 'B', status: null, x: 0, y: 0, linkDetails: [link] },
    ];

    const result = collectLinkDetails(nodes);

    expect(result.size).toBe(1);
    expect(result.get('10-20')).toEqual(link);
  });

  /**
   * NEGATIVE PROOF: nodes without a `linkDetails` field (query didn't opt in,
   * or a stale row) contribute nothing — no crash, no phantom entries.
   *
   * Mental-deletion: remove the `if (!node.linkDetails) continue` guard →
   * `for (const link of node.linkDetails)` throws on `undefined` → this test
   * would fail with a TypeError instead of an empty map.
   */
  it('skips nodes without a linkDetails field', async () => {
    const { collectLinkDetails } = await import('./WorkspaceCanvas');

    const nodes: PositionedNodeDetails[] = [
      { id: 1, type: 'task', name: 'X', status: null, x: 0, y: 0 },
    ];

    const result = collectLinkDetails(nodes);
    expect(result.size).toBe(0);
  });

  /**
   * NEGATIVE PROOF: the canonical key is orientation-independent — a link
   * where sourceId > targetId (e.g. B→A) is keyed identically to A→B.
   *
   * Mental-deletion: key edges by `${sourceId}-${targetId}` directly (drop
   * the min/max normalization) → this link would be keyed '20-10' and the
   * `result.get('10-20')` lookup would return undefined.
   */
  it('keys the canonical id independent of which endpoint is sourceId', async () => {
    const { collectLinkDetails } = await import('./WorkspaceCanvas');

    const link: NodeLink = { sourceId: 20, targetId: 10, linkType: 'Bidirectional', context: null };
    const nodes: PositionedNodeDetails[] = [
      { id: 10, type: 'task', name: 'A', status: null, x: 0, y: 0, linkDetails: [link] },
    ];

    const result = collectLinkDetails(nodes);
    expect(result.get('10-20')).toEqual(link);
  });
});

describe('markersForLinkType', () => {
  /**
   * POSITIVE PROOF: 'None' renders a plain line — no marker keys set.
   *
   * Mental-deletion: default-case a marker onto 'None' (e.g. always return
   * markerEnd) → `expect(result.markerEnd).toBeUndefined()` fails.
   */
  it("'None' produces no markers", async () => {
    const { markersForLinkType } = await import('./WorkspaceCanvas');
    const result = markersForLinkType('None');
    expect(result.markerStart).toBeUndefined();
    expect(result.markerEnd).toBeUndefined();
  });

  /**
   * POSITIVE PROOF: 'Unidirectional' places exactly ONE arrowhead — markerEnd
   * only, markerStart absent.
   *
   * Mental-deletion: swap the branch to also set markerStart → this test's
   * `markerStart` assertion fails.
   */
  it("'Unidirectional' sets markerEnd only", async () => {
    const { markersForLinkType } = await import('./WorkspaceCanvas');
    const result = markersForLinkType('Unidirectional');
    expect(result.markerStart).toBeUndefined();
    expect(result.markerEnd).toEqual({ type: MarkerType.ArrowClosed });
  });

  /**
   * POSITIVE PROOF: 'Bidirectional' places arrowheads on BOTH ends.
   *
   * Mental-deletion: revert the Bidirectional branch to the Unidirectional
   * shape (markerEnd only) → `markerStart` assertion fails.
   */
  it("'Bidirectional' sets both markerStart and markerEnd", async () => {
    const { markersForLinkType } = await import('./WorkspaceCanvas');
    const result = markersForLinkType('Bidirectional');
    expect(result.markerStart).toEqual({ type: MarkerType.ArrowClosed });
    expect(result.markerEnd).toEqual({ type: MarkerType.ArrowClosed });
  });
});

describe('joinLinkMetadata', () => {
  /**
   * POSITIVE PROOF (the orientation crux): buildEdgesFromInlineLinks assigns
   * source/target in arbitrary row-iteration order. When the true NodeLink
   * orientation is reversed relative to that arbitrary assignment,
   * joinLinkMetadata corrects source/target to match — so the arrowhead
   * (rendered at markerEnd, i.e. `target`) lands at the real target.
   *
   * Mental-deletion: drop the `source: String(link.sourceId), target:
   * String(link.targetId)` overwrite (keep the original edge's source/target)
   * → `result[0].target` stays '10' instead of flipping to '20' → the
   * assertion fails.
   */
  it('normalizes source/target to the true NodeLink orientation, even when reversed', async () => {
    const { joinLinkMetadata } = await import('./WorkspaceCanvas');

    // buildEdgesFromInlineLinks happened to iterate node 20 first, producing
    // an edge object with source='20', target='10' — but the real link is
    // 10 → 20 (Unidirectional).
    const edges = [{ id: '10-20', source: '20', target: '10', type: 'floating' }];
    const linkDetailsById = new Map<string, NodeLink>([
      ['10-20', { sourceId: 10, targetId: 20, linkType: 'Unidirectional', context: 'blocks' }],
    ]);

    const result = joinLinkMetadata(edges, linkDetailsById);

    expect(result[0].source).toBe('10');
    expect(result[0].target).toBe('20');
    expect(result[0].id).toBe('10-20');
  });

  /**
   * POSITIVE PROOF: linkType + context are attached to `data`, and the
   * corresponding markers are attached alongside (delegates to
   * markersForLinkType — verified independently above).
   *
   * Mental-deletion: omit `data` from the returned edge → `result[0].data`
   * is undefined → the `context` assertion fails.
   */
  it('attaches linkType + context to data and the matching markers', async () => {
    const { joinLinkMetadata } = await import('./WorkspaceCanvas');

    const edges = [{ id: '1-2', source: '1', target: '2', type: 'floating' }];
    const linkDetailsById = new Map<string, NodeLink>([
      ['1-2', { sourceId: 1, targetId: 2, linkType: 'Bidirectional', context: 'depends on' }],
    ]);

    const result = joinLinkMetadata(edges, linkDetailsById);

    expect(result[0].data).toEqual({ linkType: 'Bidirectional', context: 'depends on' });
    expect(result[0].markerStart).toEqual({ type: MarkerType.ArrowClosed });
    expect(result[0].markerEnd).toEqual({ type: MarkerType.ArrowClosed });
  });

  /**
   * NEGATIVE PROOF: an edge with no linkDetails match (query didn't opt in,
   * or a transient race) passes through completely unchanged — plain
   * undirected line, no data.
   *
   * Mental-deletion: remove the `if (!link) return edge as WorkspaceEdge`
   * early-return → `.map` falls through and reads `link.sourceId` on
   * `undefined` → throws, instead of returning the edge untouched.
   */
  it('passes edges through unchanged when no linkDetails match exists', async () => {
    const { joinLinkMetadata } = await import('./WorkspaceCanvas');

    const edge = { id: '1-2', source: '1', target: '2', type: 'floating' };
    const result = joinLinkMetadata([edge], new Map());

    expect(result[0]).toEqual(edge);
    expect(result[0].data).toBeUndefined();
  });

  /**
   * NEGATIVE PROOF: the canonical edge id is preserved across the
   * source/target flip, so reconcileEdges keying (by `id`) survives
   * orientation normalization.
   *
   * Mental-deletion: rebuild `id` from the normalized source/target (e.g.
   * `${link.sourceId}-${link.targetId}`) instead of spreading the original
   * edge's `id` — for a link where sourceId > targetId this would produce a
   * NON-canonical id (e.g. '20-10'), breaking the `${lo}-${hi}` invariant
   * reconcileEdges/buildEdgesFromInlineLinks rely on.
   */
  it('preserves the canonical edge id even when true orientation is high-to-low', async () => {
    const { joinLinkMetadata } = await import('./WorkspaceCanvas');

    const edges = [{ id: '10-20', source: '10', target: '20', type: 'floating' }];
    const linkDetailsById = new Map<string, NodeLink>([
      ['10-20', { sourceId: 20, targetId: 10, linkType: 'Unidirectional', context: null }],
    ]);

    const result = joinLinkMetadata(edges, linkDetailsById);

    expect(result[0].id).toBe('10-20');
    expect(result[0].source).toBe('20');
    expect(result[0].target).toBe('10');
  });
});
