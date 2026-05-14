/**
 * useProjectOrg — resolves which organisation owns a project.
 *
 * Queries ?linkedto=<projectId>&type=organization&count=1.
 * Works because the org node is linked directly to its project nodes per
 * the DiVoid topology convention (node #9).
 *
 * Returns the org's node id, or undefined when:
 *   - the project id is falsy/zero (hook is disabled).
 *   - no org link is found (topology gap — caller should surface a warning).
 *   - the query is still loading.
 *
 * Task: DiVoid node #391
 */

import { useNodeListLinkedTo } from '@/features/nodes/useNodeListLinkedTo';

export interface ProjectOrgResult {
  /** The organisation node id linked to this project, if found. */
  orgId: number | undefined;
  isLoading: boolean;
  isError: boolean;
  error: unknown;
  /** True when the query resolved and found no org link. Signals a topology gap. */
  isOrgUnknown: boolean;
}

export function useProjectOrg(projectId: number | undefined): ProjectOrgResult {
  const { data, isLoading, isError, error } = useNodeListLinkedTo(
    projectId ?? 0,
    { type: ['organization'], count: 1 },
  );

  const firstOrg = data?.result[0];
  const orgId = firstOrg?.id;

  // isOrgUnknown: query resolved (not loading, no error, project id given) but no org found.
  const isOrgUnknown =
    !isLoading && !isError && projectId !== undefined && projectId > 0 && orgId === undefined && data !== undefined;

  return { orgId, isLoading, isError, error, isOrgUnknown };
}
