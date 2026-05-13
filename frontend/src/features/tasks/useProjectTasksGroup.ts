/**
 * useProjectTasksGroup — resolves the "Tasks" group node for a given project.
 *
 * Queries GET /api/nodes?linkedto=<projectId>&name=Tasks and returns the
 * first result. If no result exists, id is undefined and hasGroup is false.
 *
 * This hook is the single source of truth for the project-tasks topology
 * (DiVoid node #9: link tasks to the project's Tasks group, not the project
 * directly). Do NOT auto-create the group if it is missing — that topology
 * decision must remain visible to the user.
 *
 * Task: DiVoid node #369
 */

import { useNodeListLinkedTo } from '@/features/nodes/useNodeListLinkedTo';

export interface ProjectTasksGroup {
  /** The Tasks group node id, or undefined when not found. */
  id: number | undefined;
  /** True when the query is resolved and a Tasks group exists. */
  hasGroup: boolean;
  /** True while the query is in flight. */
  isLoading: boolean;
  /** Populated when the query fails. */
  error: unknown;
}

export function useProjectTasksGroup(projectId: number): ProjectTasksGroup {
  const { data, isLoading, error } = useNodeListLinkedTo(projectId, {
    name: ['Tasks'],
    count: 1,
  });

  const firstResult = data?.result[0];

  return {
    id: firstResult?.id,
    hasGroup: !isLoading && !error && firstResult !== undefined,
    isLoading,
    error,
  };
}
