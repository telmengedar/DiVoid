/**
 * ProjectPillRow — one pill per project of the selected organisation.
 *
 * Data: useNodeListLinkedTo(selectedOrgId, { type: ['project'], sort: 'name' }).
 * Default-selected = the current projectId from the URL.
 *
 * When `homeProjectIds` is provided (a Set of project ids), only projects
 * whose ids are in the set are rendered — this is the client-side intersection
 * of "linked to selected org" AND "linked to home node" (option B from #400).
 * When `homeProjectIds` is null/undefined, all org-linked projects are shown.
 *
 * Clicking a pill navigates to /tasks/projects/:id.
 *
 * Task: DiVoid node #391, #400
 */

import { useNavigate } from 'react-router-dom';
import { useNodeListLinkedTo } from '@/features/nodes/useNodeListLinkedTo';
import { ROUTES } from '@/lib/constants';
import type { NodeDetails } from '@/types/divoid';

interface ProjectPillRowProps {
  /** The org whose projects are shown. 0 = disabled (no org selected yet). */
  orgId: number;
  /** The project pill that should appear selected. */
  selectedProjectId: number | undefined;
  /**
   * When provided, only projects whose ids are in this set are shown.
   * Client-side intersection of org-linked + home-node-linked projects.
   * null/undefined = no filtering (show all org projects).
   */
  homeProjectIds?: Set<number> | null;
}

export function ProjectPillRow({ orgId, selectedProjectId, homeProjectIds }: ProjectPillRowProps) {
  const navigate = useNavigate();
  const { data, isLoading, isError } = useNodeListLinkedTo(orgId, {
    type: ['project'],
    sort: 'name',
    count: 100,
  });

  if (isError) {
    return (
      <div
        role="alert"
        className="text-xs text-destructive"
      >
        Failed to load projects.
      </div>
    );
  }

  const allProjects: NodeDetails[] = data?.result ?? [];

  // Apply home-node intersection filter in-render when set is provided.
  const projects =
    homeProjectIds != null
      ? allProjects.filter((p) => homeProjectIds.has(p.id))
      : allProjects;

  return (
    <div
      className="flex flex-wrap gap-2"
      role="group"
      aria-label="Project filter"
      data-testid="project-pill-row"
    >
      {isLoading && orgId > 0 && (
        <span className="text-xs text-muted-foreground">Loading…</span>
      )}
      {orgId <= 0 && !isLoading && projects.length === 0 && (
        <span className="text-xs text-muted-foreground">Select an organisation to see its projects.</span>
      )}
      {projects.map((project) => {
        const isSelected = project.id === selectedProjectId;
        return (
          <button
            key={project.id}
            type="button"
            onClick={() => navigate(ROUTES.TASKS_PROJECT(project.id))}
            aria-pressed={isSelected}
            data-testid={`project-pill-${project.id}`}
            className={[
              'inline-flex items-center rounded-full px-3 py-1 text-xs font-medium border transition-colors',
              isSelected
                ? 'bg-primary text-primary-foreground border-primary'
                : 'bg-background text-muted-foreground border-border hover:border-foreground/40',
            ].join(' ')}
          >
            {project.name}
          </button>
        );
      })}
    </div>
  );
}
