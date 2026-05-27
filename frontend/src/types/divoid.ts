/**
 * DiVoid frontend type definitions.
 *
 * These mirror the backend DTOs exactly. No frontend-only business logic here —
 * all shapes are derived from the backend API contract (node #8).
 */

// ─── Node ─────────────────────────────────────────────────────────────────────

/** Mirrors the backend NodeDetails DTO. */
export interface NodeDetails {
  id: number;
  type: string;
  name: string;
  status: string | null;
  /** Only present on semantic search results. 0–1, higher = more relevant. */
  similarity?: number;
  contentType?: string;
}

/** Paginated response envelope from GET /api/nodes (including path-query via ?path=). */
export interface Page<T> {
  result: T[];
  /** Total count of matching records. -1 when nototal=true. */
  total: number;
  /** Cursor for the next page; absent when there are no more results. */
  continue?: number;
}

/** JSON-Patch-style operation sent to PATCH /api/nodes/{id}. */
export interface PatchOperation {
  op: 'replace' | 'add' | 'remove' | 'flag' | 'unflag' | 'embed';
  /** Path must start with a leading slash, e.g. "/status". */
  path: string;
  value?: unknown;
}

// ─── User ─────────────────────────────────────────────────────────────────────

/**
 * Mirrors the backend UserDetails DTO returned by GET /api/users/me.
 * The permissions array reflects the effective set for the authenticated
 * principal (user permissions for JWT; key permissions for API key).
 */
export interface UserDetails {
  id: number;
  name: string;
  email: string | null;
  enabled: boolean;
  createdAt: string;
  permissions: string[];
  /** Optional home node — drives org/project pill filtering and auto-redirect. */
  homeNodeId: number | null;
}

// ─── API error ────────────────────────────────────────────────────────────────

/**
 * Frontend wrapper around the backend's { code, text } error shape.
 * Extends Error so it can be thrown and caught naturally.
 */
export class DivoidApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    public readonly text: string,
  ) {
    super(`DiVoid API error ${status}: ${code} — ${text}`);
    this.name = 'DivoidApiError';
  }
}

// ─── Node filter ──────────────────────────────────────────────────────────────

/** Query parameters accepted by GET /api/nodes. */
export interface NodeFilter {
  id?: number[];
  type?: string[];
  name?: string[];
  status?: string[];
  linkedto?: number[];
  nostatus?: boolean;
  nototal?: boolean;
  count?: number;
  continue?: number;
  sort?: 'id' | 'type' | 'name' | 'status';
  descending?: boolean;
  fields?: string[];
  /** Semantic search query. Triggers vector similarity ranking. */
  query?: string;
  /**
   * Viewport bounding rectangle [xMin, yMin, xMax, yMax] (world units, inclusive).
   * Only nodes whose X/Y fall inside the rectangle are returned.
   * PR 4b: used by workspace graph view.
   */
  bounds?: [number, number, number, number];
}

/** A link between two nodes, returned by GET /api/nodes/links. */
export interface NodeLink {
  sourceId: number;
  targetId: number;
}

// ─── Type catalog ─────────────────────────────────────────────────────────────

/**
 * One entry from GET /api/types.
 *
 * NOTE: the `type` field is absent (not null, truly absent) for structural
 * group nodes whose underlying NodeType row has an empty/null name. These
 * 13 entries (Tasks, Docs, Types groups etc.) are the graph's untyped nodes.
 * See DiVoid task #486 for the empirical count and handling decision.
 */
export interface NodeTypeEntry {
  id: number;
  /** Absent for structural/untyped rows — use `?? undefined` to detect. */
  type?: string;
  count: number;
}

/** Extended NodeDetails with required x/y position (workspace view). */
export interface PositionedNodeDetails extends NodeDetails {
  x: number;
  y: number;
  /**
   * Inline neighbor ids — present when the viewport query includes fields=links.
   * An empty array means the node has no incident links (backend contract: DiVoid #1213).
   * Absent when the query did not opt into fields=links.
   */
  links?: number[];
}
