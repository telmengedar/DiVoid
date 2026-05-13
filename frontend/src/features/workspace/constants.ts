/**
 * Workspace view constants.
 *
 * NODE_DIMENSION_PADDING: how much to expand the queried bounds beyond the
 * visible viewport on all four sides to compensate for the fact that the
 * backend's bounds filter is a center-point-in-bounds check. A node whose
 * center is just outside the viewport but whose rendered rectangle intrudes
 * would pop in/out at the edge without this padding.
 *
 * Value: 80px — the xyflow default node width/height is 150×40px; we use 80
 * as a conservative "half-diagonal" that covers all default sizes with margin.
 * This is deliberately generous: the cost is a slightly larger SQL predicate
 * at query time; the UX cost of pop-in is higher.
 *
 * Where this constant is consumed: WorkspaceCanvas.tsx, computePaddedBounds().
 *
 * DiVoid Rule 3 documentation node: filed as DiVoid node #<bounds-padding-doc>.
 * See: docs/architecture/workspace-mode.md §bounds-padding.
 */

/** Pixel padding added to all four sides of the viewport before querying bounds. */
export const NODE_DIMENSION_PADDING = 80;

/** Debounce delay (ms) before a viewport change triggers a new API call. */
export const VIEWPORT_DEBOUNCE_MS = 250;

/** Initial viewport bounds when no sessionStorage state is present. */
export const DEFAULT_VIEWPORT_BOUNDS: [number, number, number, number] = [-500, -500, 500, 500];

/** Default initial zoom level. */
export const DEFAULT_ZOOM = 1;

/** Default initial pan position (canvas origin at screen centre). */
export const DEFAULT_PAN = { x: 0, y: 0 };
