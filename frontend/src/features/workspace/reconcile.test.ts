/**
 * Load-bearing pure-function tests for reconcile.ts (DiVoid #1261 / #275).
 *
 * Each test has a mental-deletion check — what production line to revert to
 * cause it to fail — documented inline.
 *
 * Tests:
 *  R1. reconcileNodes — identity preservation (no-op refetch returns prev ref)
 *  R2. reconcileNodes — single changed node (other refs preserved)
 *  R3. reconcileNodes — drag-preservation (position kept from prev)
 *  R4. reconcileEdges — identity preservation
 *  R5. reconcileEdges — changed edge (other refs preserved)
 *  R6. reconcileNodes — dragging node scrolled out of viewport is retained
 *  R7. reconcileNodes — links array equality (same ids = reuse ref)
 */

import { describe, it, expect } from 'vitest';
import { reconcileNodes, reconcileEdges } from './reconcile';
import type { WorkspaceNode } from './NodeCardRenderer';
import type { Edge } from '@xyflow/react';

// ─── Helpers ──────────────────────────────────────────────────────────────────

function makeNode(
  id: string,
  overrides?: Partial<WorkspaceNode> & { name?: string; status?: string | null; links?: number[] },
): WorkspaceNode {
  const { name = `Node ${id}`, status = null, links, ...rest } = overrides ?? {};
  return {
    id,
    type: 'nodeCard',
    position: { x: 0, y: 0 },
    data: {
      id: Number(id),
      type: 'task',
      name,
      status,
      contentType: undefined,
      links: links ?? [],
      x: 0,
      y: 0,
      onPeek: () => {},
    } as WorkspaceNode['data'],
    ...rest,
  };
}

function makeEdge(id: string, source = 'a', target = 'b'): Edge {
  return { id, source, target, type: 'floating' };
}

// ─── reconcileNodes ───────────────────────────────────────────────────────────

describe('reconcileNodes', () => {
  /**
   * R1: Identity preservation — feeding identical nodes back returns the same
   * array reference (bail-out), preventing React from scheduling a re-render.
   *
   * Mental-deletion: remove the `!changed → return prev` bail-out in
   * reconcileNodes → returns a new array even though nothing changed → reference
   * equality assertion (`toBe(prev)`) fails.
   */
  it('R1: returns prev reference when all nodes are structurally unchanged', () => {
    const prev = [makeNode('1'), makeNode('2'), makeNode('3')];
    const incoming = prev.map((n) => ({ ...n, data: { ...n.data } })); // fresh refs, same values

    const result = reconcileNodes(prev, incoming, new Set());

    expect(result).toBe(prev);
  });

  /**
   * R2: Single change — only the changed node gets a new reference; the other
   * four nodes reuse their prev references.
   *
   * Mental-deletion: remove the `nodeDataEqual` reuse path → all five nodes
   * return `incomingNode` → reference equality checks on nodes 0,1,3,4 fail.
   */
  it('R2: reuses prev references for unchanged nodes; only changed node is new', () => {
    const node1 = makeNode('1');
    const node2 = makeNode('2');
    const node3 = makeNode('3', { name: 'Original Name' });
    const node4 = makeNode('4');
    const node5 = makeNode('5');
    const prev = [node1, node2, node3, node4, node5];

    // Only node3's name changes.
    const incoming = [
      { ...node1, data: { ...node1.data } },
      { ...node2, data: { ...node2.data } },
      makeNode('3', { name: 'Updated Name' }),
      { ...node4, data: { ...node4.data } },
      { ...node5, data: { ...node5.data } },
    ];

    const result = reconcileNodes(prev, incoming, new Set());

    expect(result).toHaveLength(5);
    expect(result[0]).toBe(node1);
    expect(result[1]).toBe(node2);
    expect(result[2]).not.toBe(node3);
    expect(result[2].data.name).toBe('Updated Name');
    expect(result[3]).toBe(node4);
    expect(result[4]).toBe(node5);
  });

  /**
   * R3: Drag-preservation — a dragging node's position is preserved from prev;
   * only data fields are updated when they change.
   *
   * Mental-deletion: remove the `draggingIds.has(existing.id)` branch →
   * dragging node takes incoming position → position assertion fails.
   */
  it('R3: preserves prev position for dragging nodes', () => {
    const draggingNode = makeNode('2', {
      position: { x: 500, y: 500 },
      dragging: true,
    } as Partial<WorkspaceNode>);
    draggingNode.position = { x: 500, y: 500 };

    const prev = [makeNode('1'), draggingNode, makeNode('3')];

    // Server still reports old position (50, 50) for node 2.
    const incoming = [
      makeNode('1'),
      makeNode('2'), // position { x:0, y:0 } — should be discarded for dragging node
      makeNode('3'),
    ];

    const draggingIds = new Set(['2']);
    const result = reconcileNodes(prev, incoming, draggingIds);

    const resultNode2 = result.find((n) => n.id === '2')!;
    expect(resultNode2.position).toEqual({ x: 500, y: 500 });
  });

  /**
   * R6: A dragging node that scrolled out of the incoming viewport is retained
   * in the result (drag-out preservation).
   *
   * Mental-deletion: remove the `draggingOut.concat` tail → the dragging node
   * disappears from the result → length assertion fails.
   */
  it('R6: retains dragging nodes that scrolled out of viewport', () => {
    const draggingNode = makeNode('99', {
      dragging: true,
    } as Partial<WorkspaceNode>);

    const prev = [makeNode('1'), draggingNode];
    const incoming = [makeNode('1')]; // node 99 is outside new viewport bounds

    const draggingIds = new Set(['99']);
    const result = reconcileNodes(prev, incoming, draggingIds);

    expect(result).toHaveLength(2);
    expect(result.find((n) => n.id === '99')).toBe(draggingNode);
  });

  /**
   * R7: Nodes with the same links array contents (same ids, same order)
   * are treated as equal — prev reference is reused, no new allocation.
   *
   * Mental-deletion: remove the linksEqual check (use reference comparison
   * instead) → every refetch that allocates a new links array creates a new
   * data reference → node3 does not match `toBe(node3)` → fails.
   */
  it('R7: reuses prev reference when links array has same contents', () => {
    const node = makeNode('1', { links: [10, 20, 30] });
    const prev = [node];

    // Fresh array object, same values.
    const incoming = [makeNode('1', { links: [10, 20, 30] })];

    const result = reconcileNodes(prev, incoming, new Set());

    expect(result).toBe(prev);
    expect(result[0]).toBe(node);
  });
});

// ─── reconcileEdges ───────────────────────────────────────────────────────────

describe('reconcileEdges', () => {
  /**
   * R4: Identity preservation — identical edge set returns prev reference.
   *
   * Mental-deletion: remove the `!changed → return prev` bail-out →
   * returns new array even when unchanged → `toBe(prev)` fails.
   */
  it('R4: returns prev reference when all edges are structurally unchanged', () => {
    const prev = [makeEdge('1-2', '1', '2'), makeEdge('3-4', '3', '4')];
    // Fresh references, same values.
    const incoming = prev.map((e) => ({ ...e }));

    const result = reconcileEdges(prev, incoming);

    expect(result).toBe(prev);
  });

  /**
   * R5: Changed edge — the changed edge returns incoming; others return prev ref.
   *
   * Mental-deletion: remove the prev-reference reuse inside reconcileEdges map
   * → all edges are new refs → edge[0] `toBe(edge1)` fails.
   */
  it('R5: reuses prev references for unchanged edges; only changed edge is new', () => {
    const edge1 = makeEdge('1-2', '1', '2');
    const edge2 = makeEdge('3-4', '3', '4');
    const prev = [edge1, edge2];

    // edge2 target changes.
    const incoming = [{ ...edge1 }, makeEdge('3-4', '3', '99')];

    const result = reconcileEdges(prev, incoming);

    expect(result[0]).toBe(edge1);
    expect(result[1]).not.toBe(edge2);
    expect(result[1].target).toBe('99');
  });
});
