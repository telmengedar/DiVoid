/**
 * Pure reconciliation helpers for workspace node/edge references.
 *
 * Problem: every viewport refetch allocates new WorkspaceNode / Edge objects
 * even when nothing changed. xyflow's internal store sees new references and
 * processes them as updates, causing the "scroll-blink" regression (#1261).
 *
 * Fix: instead of replacing the prev array wholesale, compare each incoming
 * entry against prev and return the prev reference when nothing changed.
 * When the reconciled array is identical to prev (all references equal, same
 * length), return prev itself so React's setState functional-updater bails out
 * and no re-render occurs.
 *
 * DiVoid task: #1261
 */

import type { Edge } from '@xyflow/react';
import type { WorkspaceNode } from './NodeCardRenderer';

// ─── Link equality ────────────────────────────────────────────────────────────

/**
 * Returns true when two links arrays contain the same ids in the same order.
 * Order matters because xyflow may use array position for edge lookup; if order
 * is unreliable, sort before comparing. The current backend preserves insertion
 * order — same-order comparison is sufficient and avoids a sort allocation.
 */
function linksEqual(a: number[] | undefined, b: number[] | undefined): boolean {
  if (a === b) return true;
  if (!a || !b) return a === b;
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) {
    if (a[i] !== b[i]) return false;
  }
  return true;
}

// ─── Node equality ────────────────────────────────────────────────────────────

/** Fields compared to decide whether a node's data is structurally unchanged. */
function nodeDataEqual(prev: WorkspaceNode, incoming: WorkspaceNode): boolean {
  const pd = prev.data;
  const id = incoming.data;
  return (
    prev.type === incoming.type &&
    prev.position.x === incoming.position.x &&
    prev.position.y === incoming.position.y &&
    pd.id === id.id &&
    pd.type === id.type &&
    pd.name === id.name &&
    pd.status === id.status &&
    pd.contentType === id.contentType &&
    linksEqual(pd.links, id.links)
  );
}

// ─── reconcileNodes ───────────────────────────────────────────────────────────

/**
 * Reference-preserving merge of incoming WorkspaceNodes against prev state.
 *
 * - Dragging nodes: position preserved from prev; data updated only when
 *   changed. Dragging nodes that scrolled out of viewport are retained.
 * - Non-dragging nodes: if structurally equal to prev, prev reference reused.
 *   Otherwise incoming reference used.
 * - Bail-out: if every entry is reference-equal to prev and length matches,
 *   returns prev (exact array reference) so React's setState skips the update.
 *
 * The `draggingIds` set is built from prev inside the setNodes functional
 * updater (see WorkspaceCanvas) and passed in here.
 */
export function reconcileNodes(
  prev: WorkspaceNode[],
  incoming: WorkspaceNode[],
  draggingIds: Set<string>,
): WorkspaceNode[] {
  const prevMap = new Map(prev.map((p) => [p.id, p]));
  const incomingMap = new Map(incoming.map((n) => [n.id, n]));

  let changed = incoming.length !== prev.length;

  const result: WorkspaceNode[] = incoming.map((incomingNode) => {
    const existing = prevMap.get(incomingNode.id);

    if (existing && draggingIds.has(existing.id)) {
      // Dragging: preserve position; update data only when it changed.
      const dataChanged =
        existing.data.name !== incomingNode.data.name ||
        existing.data.status !== incomingNode.data.status ||
        existing.data.type !== incomingNode.data.type ||
        existing.data.contentType !== incomingNode.data.contentType ||
        !linksEqual(existing.data.links, incomingNode.data.links);

      const next: WorkspaceNode = dataChanged
        ? { ...existing, data: incomingNode.data }
        : existing;

      if (next !== existing) changed = true;
      return next;
    }

    if (existing && nodeDataEqual(existing, incomingNode)) {
      // Structurally unchanged — reuse prev reference.
      return existing;
    }

    // New or changed node.
    changed = true;
    return incomingNode;
  });

  // Retain dragging nodes that scrolled out of viewport bounds temporarily.
  const draggingOut = prev.filter((p) => draggingIds.has(p.id) && !incomingMap.has(p.id));
  if (draggingOut.length > 0) {
    changed = true;
    return result.concat(draggingOut);
  }

  // Bail-out: if nothing changed and result is same length, return prev.
  if (!changed) return prev;
  return result;
}

// ─── reconcileEdges ───────────────────────────────────────────────────────────

/**
 * Reference-preserving merge of incoming Edges against prev state.
 *
 * For each incoming edge, if a prev entry with the same id exists AND has
 * identical source/target/type: return the prev reference. Otherwise incoming.
 * Bail-out: if the result would be reference-equal to prev on every entry,
 * returns prev so React's setState skips the update.
 */
export function reconcileEdges(prev: Edge[], incoming: Edge[]): Edge[] {
  const prevMap = new Map(prev.map((e) => [e.id, e]));

  let changed = incoming.length !== prev.length;

  const result = incoming.map((incomingEdge) => {
    const existing = prevMap.get(incomingEdge.id);
    if (
      existing &&
      existing.source === incomingEdge.source &&
      existing.target === incomingEdge.target &&
      existing.type === incomingEdge.type
    ) {
      return existing;
    }
    changed = true;
    return incomingEdge;
  });

  if (!changed) return prev;
  return result;
}
