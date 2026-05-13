/**
 * FloatingEdge — center-to-bounding-box-intersection edge renderer.
 *
 * @xyflow/react does not ship a "floating" edge type. This implements the
 * "Simple Floating Edges" pattern from reactflow.dev:
 *
 *   1. useInternalNode(id) provides positionAbsolute + measured dimensions
 *      for both the source and target nodes.
 *   2. getEdgeParams() computes the nearest intersection point between the
 *      straight center-to-center line and each node's bounding rectangle.
 *   3. getStraightPath() produces an SVG path string between those points.
 *   4. <BaseEdge> renders it.
 *
 * Because the edge anchors to the intersection of the line and the node's
 * bounding box (not to a specific Handle), handle positions are irrelevant
 * and the existing invisible handles in NodeCardRenderer are untouched.
 *
 * Design: docs/architecture/workspace-mode.md §5.7
 * Task: DiVoid node #352
 */

import { useInternalNode, BaseEdge, getStraightPath, type EdgeProps } from '@xyflow/react';

// ─── Geometry helpers ─────────────────────────────────────────────────────────

export interface NodeRect {
  x: number;
  y: number;
  width: number;
  height: number;
}

interface EdgeParams {
  sx: number;
  sy: number;
  tx: number;
  ty: number;
}

/**
 * Returns the point where a line from (cx, cy) to (ox, oy) intersects the
 * boundary of a rectangle with top-left at (rx, ry) and given width/height.
 *
 * Falls back to the rectangle's center when the segment is degenerate (zero
 * length) so the edge still renders rather than crashing.
 *
 * Exported for unit testing (DiVoid task #352, rule #275).
 */
export function getIntersectionPoint(
  rect: NodeRect,
  targetX: number,
  targetY: number,
): { x: number; y: number } {
  const cx = rect.x + rect.width / 2;
  const cy = rect.y + rect.height / 2;

  const dx = targetX - cx;
  const dy = targetY - cy;

  // Degenerate: source and target share the same center point.
  if (dx === 0 && dy === 0) return { x: cx, y: cy };

  const hw = rect.width / 2;
  const hh = rect.height / 2;

  // Clip line from center (cx,cy) toward (targetX, targetY) against the rect.
  // For each side compute the parametric t where the line hits, take the smallest
  // positive t that still lies within the rect's perpendicular extent.
  const candidates: number[] = [];

  if (dx !== 0) {
    const tRight = hw / dx;
    if (tRight > 0 && Math.abs(dy * tRight) <= hh) candidates.push(tRight);
    const tLeft = -hw / dx;
    if (tLeft > 0 && Math.abs(dy * tLeft) <= hh) candidates.push(tLeft);
  }
  if (dy !== 0) {
    const tBottom = hh / dy;
    if (tBottom > 0 && Math.abs(dx * tBottom) <= hw) candidates.push(tBottom);
    const tTop = -hh / dy;
    if (tTop > 0 && Math.abs(dx * tTop) <= hw) candidates.push(tTop);
  }

  if (candidates.length === 0) return { x: cx, y: cy };

  const t = Math.min(...candidates);
  return { x: cx + dx * t, y: cy + dy * t };
}

/**
 * Given two InternalNode rects, returns the intersection points on each rect's
 * boundary along the center-to-center axis.
 *
 * Exported for unit testing (DiVoid task #352, rule #275).
 */
export function getEdgeParams(source: NodeRect, target: NodeRect): EdgeParams {
  const sourceCx = source.x + source.width / 2;
  const sourceCy = source.y + source.height / 2;
  const targetCx = target.x + target.width / 2;
  const targetCy = target.y + target.height / 2;

  const { x: sx, y: sy } = getIntersectionPoint(source, targetCx, targetCy);
  const { x: tx, y: ty } = getIntersectionPoint(target, sourceCx, sourceCy);

  return { sx, sy, tx, ty };
}

// ─── Component ────────────────────────────────────────────────────────────────

/**
 * FloatingEdge renders a straight SVG path between the nearest bounding-box
 * intersection points of the source and target nodes.
 *
 * The component is registered as `edgeTypes.floating` in WorkspaceCanvas.
 * Edges must be created with `type: 'floating'` to use this renderer.
 */
export function FloatingEdge({ id, source, target, markerEnd, style }: EdgeProps) {
  const sourceNode = useInternalNode(source);
  const targetNode = useInternalNode(target);

  // Both nodes must be mounted and measured for us to draw the edge.
  if (!sourceNode || !targetNode) return null;

  const sourceW = sourceNode.measured.width ?? 0;
  const sourceH = sourceNode.measured.height ?? 0;
  const targetW = targetNode.measured.width ?? 0;
  const targetH = targetNode.measured.height ?? 0;

  if (sourceW === 0 || sourceH === 0 || targetW === 0 || targetH === 0) return null;

  const sourceRect: NodeRect = {
    x: sourceNode.internals.positionAbsolute.x,
    y: sourceNode.internals.positionAbsolute.y,
    width: sourceW,
    height: sourceH,
  };
  const targetRect: NodeRect = {
    x: targetNode.internals.positionAbsolute.x,
    y: targetNode.internals.positionAbsolute.y,
    width: targetW,
    height: targetH,
  };

  const { sx, sy, tx, ty } = getEdgeParams(sourceRect, targetRect);

  const [edgePath] = getStraightPath({ sourceX: sx, sourceY: sy, targetX: tx, targetY: ty });

  return (
    <BaseEdge
      id={id}
      path={edgePath}
      markerEnd={markerEnd}
      style={style}
    />
  );
}
