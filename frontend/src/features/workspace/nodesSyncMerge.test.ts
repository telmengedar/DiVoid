/**
 * Unit test for win #6: Map-based prev-merge in the nodes-sync useEffect.
 *
 * The logic inside the setNodes updater function is a pure transformation:
 *   (prev, xyNodes) => merged[]
 *
 * We extract and test it directly to verify the three invariants:
 *   1. Non-dragging nodes receive their incoming position (positions preserved).
 *   2. New nodes (not in prev) are appended.
 *   3. Nodes that left the viewport (not in incoming) are dropped — unless
 *      they are actively dragging, in which case they are kept.
 *
 * DiVoid task #343 win #6.
 */

import { describe, it, expect } from 'vitest';
import type { WorkspaceNode } from './NodeCardRenderer';

// ─── Extracted pure function ──────────────────────────────────────────────────
//
// This is the exact logic from the setNodes updater in WorkspaceCanvas, pulled
// into a testable function. If the component logic changes, this function must
// be kept in sync.

function mergeNodes(
  prev: WorkspaceNode[],
  incoming: WorkspaceNode[],
): WorkspaceNode[] {
  const dragging = new Set(prev.filter((n) => n.dragging).map((n) => n.id));
  const prevMap  = new Map(prev.map((p) => [p.id, p]));
  const incomingMap = new Map(incoming.map((n) => [n.id, n]));

  return incoming.map((incomingNode) => {
    const existing = prevMap.get(incomingNode.id);
    if (existing && dragging.has(existing.id)) {
      return { ...existing, data: incomingNode.data };
    }
    return incomingNode;
  }).concat(
    prev.filter((p) => dragging.has(p.id) && !incomingMap.has(p.id)),
  );
}

// ─── Helpers ─────────────────────────────────────────────────────────────────

function makeNode(
  id: string,
  x: number,
  y: number,
  overrides?: Partial<WorkspaceNode>,
): WorkspaceNode {
  return {
    id,
    type: 'nodeCard',
    position: { x, y },
    data: { id: Number(id), type: 'task', name: `Node ${id}`, status: null, x, y } as WorkspaceNode['data'],
    ...overrides,
  };
}

// ─── Tests ────────────────────────────────────────────────────────────────────

describe('nodesSyncMerge — Map-based prev-merge (win #6)', () => {
  it('updates positions of non-dragging nodes from incoming', () => {
    const prev     = [makeNode('1', 100, 200), makeNode('2', 300, 400)];
    const incoming = [makeNode('1', 110, 210), makeNode('2', 310, 410)];

    const result = mergeNodes(prev, incoming);

    expect(result).toHaveLength(2);
    // Positions come from incoming (server-authoritative).
    expect(result[0].position).toEqual({ x: 110, y: 210 });
    expect(result[1].position).toEqual({ x: 310, y: 410 });
  });

  it('appends new nodes that were not in prev', () => {
    const prev     = [makeNode('1', 0, 0)];
    const incoming = [makeNode('1', 0, 0), makeNode('99', 500, 600)];

    const result = mergeNodes(prev, incoming);

    expect(result).toHaveLength(2);
    const ids = result.map((n) => n.id);
    expect(ids).toContain('99');
  });

  it('drops nodes that left the viewport (not in incoming, not dragging)', () => {
    const prev     = [makeNode('1', 0, 0), makeNode('2', 0, 0)];
    const incoming = [makeNode('1', 0, 0)]; // node 2 scrolled out

    const result = mergeNodes(prev, incoming);

    expect(result).toHaveLength(1);
    expect(result[0].id).toBe('1');
  });

  it('preserves dragging node position when incoming has different position', () => {
    const draggingNode = makeNode('1', 100, 200, { dragging: true });
    const prev         = [draggingNode];
    // Server still reports old position — client has moved it
    const incoming     = [makeNode('1', 50, 50)];

    const result = mergeNodes(prev, incoming);

    expect(result).toHaveLength(1);
    // Position preserved from prev (drag is in progress)
    expect(result[0].position).toEqual({ x: 100, y: 200 });
  });

  it('keeps a dragging node that scrolled out of viewport bounds', () => {
    const draggingNode = makeNode('1', 9999, 9999, { dragging: true });
    const prev         = [draggingNode, makeNode('2', 0, 0)];
    // Node 1 is outside the new viewport bounds — not in incoming
    const incoming     = [makeNode('2', 0, 0)];

    const result = mergeNodes(prev, incoming);

    expect(result).toHaveLength(2);
    const ids = result.map((n) => n.id);
    expect(ids).toContain('1'); // kept even though out of bounds
    expect(ids).toContain('2');
  });

  it('handles empty prev (initial mount)', () => {
    const prev     : WorkspaceNode[] = [];
    const incoming  = [makeNode('1', 0, 0), makeNode('2', 100, 100)];

    const result = mergeNodes(prev, incoming);

    expect(result).toHaveLength(2);
    expect(result.map((n) => n.id)).toEqual(['1', '2']);
  });

  it('handles empty incoming (all nodes scrolled out, none dragging)', () => {
    const prev     = [makeNode('1', 0, 0), makeNode('2', 100, 100)];
    const incoming : WorkspaceNode[] = [];

    const result = mergeNodes(prev, incoming);

    expect(result).toHaveLength(0);
  });
});
