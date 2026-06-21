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
import type { NodeAccess } from '@/types/divoid';

// ─── Access vocabulary ────────────────────────────────────────────────────────

/**
 * The four canonical per-node access values, in display order.
 * Mirrors the backend NodeAccess enum serialized by JsonStringEnumConverter.
 * See NodeAccess.cs and DiVoid #1370 / PR #130.
 */
export const ACCESS_OPTIONS: NodeAccess[] = ['None', 'Read', 'Write', 'Read, Write'];

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
  /**
   * Type is optional — an empty/absent type creates an untyped node.
   * The backend normalises null/""/whitespace → untyped (NodeType.Type IS NULL)
   * as of PR #148 (DiVoid #2011 / design #2014).
   */
  type: z.string(),
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
  /**
   * Per-node access value. Only submitted when the caller is owner or admin
   * (the `canEditAccess` gate in EditNodeDialog prevents the field rendering otherwise).
   * Backend rejects the PATCH with 403 if the caller lacks owner/admin permission.
   */
  access: z.enum(['None', 'Read', 'Write', 'Read, Write']).optional(),
});

export type EditNodeFormValues = z.infer<typeof editNodeSchema>;

// ─── Link-search schema ───────────────────────────────────────────────────────

export const linkSearchSchema = z.object({
  query: z.string().min(1, 'Enter a search term'),
});

export type LinkSearchFormValues = z.infer<typeof linkSearchSchema>;
