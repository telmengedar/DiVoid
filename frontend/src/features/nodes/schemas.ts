/**
 * Zod schemas for node write-path forms.
 *
 * These schemas validate client-side input before it is sent to the backend.
 * They are intentionally permissive (no backend-specific rules) — the backend
 * is the source of truth for business rules. Client validation is UX only.
 *
 * Status vocabulary per DiVoid node #9:
 *  - task: new | open | in-progress | closed
 *  - bug:  new | open | in-progress | fixed
 *  - other types: no status (null)
 *
 * Design: docs/architecture/frontend-bootstrap.md §5.6
 * Task: DiVoid node #229
 */

import { z } from 'zod';

// ─── Status vocabulary ────────────────────────────────────────────────────────

/** Canonical status values for `task` nodes (DiVoid node #9). */
export const TASK_STATUSES = ['new', 'open', 'in-progress', 'closed'] as const;
export type TaskStatus = (typeof TASK_STATUSES)[number];

/** Canonical status values for `bug` nodes (DiVoid node #9). */
export const BUG_STATUSES = ['new', 'open', 'in-progress', 'fixed'] as const;
export type BugStatus = (typeof BUG_STATUSES)[number];

/** Types that carry a status field. */
export const STATUS_BEARING_TYPES = ['task', 'bug'] as const;

/**
 * Returns the canonical status options for a given node type.
 * Returns null for types that do not carry a status.
 */
export function statusOptionsForType(
  type: string,
): typeof TASK_STATUSES | typeof BUG_STATUSES | null {
  if (type === 'task') return TASK_STATUSES;
  if (type === 'bug') return BUG_STATUSES;
  return null;
}

// ─── Create node schema ───────────────────────────────────────────────────────

export const createNodeSchema = z.object({
  type: z.string().min(1, 'Type is required'),
  name: z.string().min(1, 'Name is required').max(255, 'Name must be 255 characters or fewer'),
  /**
   * Status is optional at the schema level; the form component conditionally
   * renders the status dropdown only when type is task or bug.
   */
  status: z.string().optional(),
});

export type CreateNodeFormValues = z.infer<typeof createNodeSchema>;

// ─── Edit node schema ─────────────────────────────────────────────────────────

export const editNodeSchema = z.object({
  name: z.string().min(1, 'Name is required').max(255, 'Name must be 255 characters or fewer'),
  /** Status is kept as a string (can be empty string to represent "no status"). */
  status: z.string().optional(),
});

export type EditNodeFormValues = z.infer<typeof editNodeSchema>;

// ─── Link-search schema ───────────────────────────────────────────────────────

export const linkSearchSchema = z.object({
  query: z.string().min(1, 'Enter a search term'),
});

export type LinkSearchFormValues = z.infer<typeof linkSearchSchema>;
