import type { NodeAccess } from '@/types/divoid';

/** Independent grants in backend bit order, with explicit audience labels. */
export const NODE_ACCESS_FLAGS = [
  { bit: 1, name: 'Read', label: 'Global read' },
  { bit: 2, name: 'Write', label: 'Global write' },
  { bit: 4, name: 'OrgRead', label: 'Organization read' },
  { bit: 8, name: 'OrgWrite', label: 'Organization write' },
] as const;

/** Decode the JSON enum representation without discarding unnamed integer bits.
 * @param access Backend enum names or integer mask.
 */
export function nodeAccessMask(access: NodeAccess): number {
  if (typeof access === 'number') return access;
  return access.split(',').reduce((mask, name) =>
    mask | (NODE_ACCESS_FLAGS.find((flag) => flag.name === name.trim())?.bit ?? 0), 0);
}

/** Label grants without implying that write includes read or organization membership.
 * @param access Backend enum names or integer mask.
 */
export function formatNodeAccess(access: NodeAccess): string {
  const mask = nodeAccessMask(access);
  const labels: string[] = NODE_ACCESS_FLAGS.filter((flag) => (mask & flag.bit) !== 0)
    .map((flag) => flag.label);
  const unknown = mask & ~15;
  if (unknown !== 0) labels.push(`Other flags (${unknown})`);
  return labels.join(', ') || 'None';
}
