/**
 * TasksPage — inline org + project pill rows for one-click task switching.
 *
 * Routes:
 *  - /tasks                  → empty-state landing (pill rows visible, no task list).
 *  - /tasks/projects/:projectId → pill rows + task list for the given project.
 *
 * Pill row behaviour (DiVoid task #391):
 *  - OrgPillRow: one pill per org. Default-selected = project's parent org.
 *  - ProjectPillRow: one pill per project of the selected org.
 *  - Clicking an org pill repopulates the project pill row; does NOT auto-navigate.
 *  - If the user selects an org whose projects don't include the current project,
 *    the task list is suppressed with an inline "Select a <org> project…" message.
 *
 * Replaces the old drill-down: OrgListView → ProjectListView → TaskListView.
 * Those components are deleted. /tasks/orgs/:orgId redirects to /tasks (see routes.tsx).
 */

import { useState, useEffect } from 'react';
import { useParams } from 'react-router-dom';
import { OrgPillRow } from './OrgPillRow';
import { ProjectPillRow } from './ProjectPillRow';
import { TaskListView } from './TaskListView';
import { useProjectOrg } from './useProjectOrg';

export function TasksPage() {
  const { projectId: projectIdParam } = useParams<{ projectId?: string }>();
  const parsedProjectId = projectIdParam ? parseInt(projectIdParam, 10) : undefined;

  // Resolve which org the current project belongs to.
  const { orgId: projectOrgId, isOrgUnknown } = useProjectOrg(parsedProjectId);

  // Track which org the user has selected via the org pill row.
  // Initialises to undefined; synced from the project's org once resolved.
  const [selectedOrgId, setSelectedOrgId] = useState<number | undefined>(undefined);

  // When the project's org resolves, sync selectedOrgId to it (once only —
  // after that the user's pill clicks take over).
  useEffect(() => {
    if (projectOrgId !== undefined && selectedOrgId === undefined) {
      setSelectedOrgId(projectOrgId);
    }
  }, [projectOrgId, selectedOrgId]);

  // When projectId changes (e.g. user navigates directly to a new project URL),
  // reset the selected org so we re-sync from the new project's org.
  useEffect(() => {
    setSelectedOrgId(undefined);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [parsedProjectId]);

  // Determine whether the current project belongs to the selected org.
  // "mismatch" means: we have a project loaded AND an org selected AND the project's
  // actual org does not match the user's selected org pill.
  const orgMismatch =
    parsedProjectId !== undefined &&
    selectedOrgId !== undefined &&
    projectOrgId !== undefined &&
    selectedOrgId !== projectOrgId;

  return (
    <div className="flex flex-col gap-4 p-4">
      <h1 className="text-xl font-semibold">Tasks</h1>

      {/* Org + project pill rows */}
      <div className="flex flex-col gap-2">
        <OrgPillRow
          selectedOrgId={selectedOrgId}
          onOrgSelect={setSelectedOrgId}
          showTopologyWarning={isOrgUnknown}
        />
        <ProjectPillRow
          orgId={selectedOrgId ?? 0}
          selectedProjectId={orgMismatch ? undefined : parsedProjectId}
        />
      </div>

      {/* Main content area */}
      {parsedProjectId === undefined ? (
        /* /tasks empty-state landing — no task list */
        <p
          className="text-sm text-muted-foreground"
          data-testid="tasks-empty-state"
        >
          Select a project above to view its tasks.
        </p>
      ) : orgMismatch ? (
        /* Org changed to one that doesn't contain the current project */
        <p
          className="text-sm text-muted-foreground"
          data-testid="org-mismatch-message"
        >
          Select a project to see its tasks.
        </p>
      ) : (
        <TaskListView
          key={parsedProjectId}
          projectId={parsedProjectId}
        />
      )}
    </div>
  );
}
