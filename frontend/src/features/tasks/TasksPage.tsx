/**
 * TasksPage — inline org + project pill rows for one-click task switching.
 *
 * Routes:
 *  - /tasks                  → empty-state landing (pill rows visible, no task list).
 *  - /tasks/projects/:projectId → pill rows + task list for the given project.
 *
 * Pill row behaviour (DiVoid task #391, #400):
 *  - OrgPillRow: one pill per org linked to home node (if set) or all orgs.
 *  - ProjectPillRow: one pill per project of the selected org, intersected with
 *    home-node-linked projects when a home node is set.
 *  - Clicking an org pill repopulates the project pill row; does NOT auto-navigate.
 *  - If the user selects an org whose projects don't include the current project,
 *    the task list is suppressed with an inline "Select a <org> project…" message.
 *
 * Auto-redirect (DiVoid task #400):
 *  - When /tasks is loaded with no projectId, homeNodeId is set, and home-linked
 *    projects resolve non-empty, navigates to the first project (replace: true).
 *  - Guards: already-redirected ref, existing projectId in URL, null homeNodeId,
 *    empty home-projects set.
 *
 * Replaces the old drill-down: OrgListView → ProjectListView → TaskListView.
 * Those components are deleted. /tasks/orgs/:orgId redirects to /tasks (see routes.tsx).
 */

import { useState, useEffect, useMemo, useRef } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { OrgPillRow } from './OrgPillRow';
import { ProjectPillRow } from './ProjectPillRow';
import { TaskListView } from './TaskListView';
import { useProjectOrg } from './useProjectOrg';
import { useWhoami } from '@/features/auth/useWhoami';
import { useNodeListLinkedTo } from '@/features/nodes/useNodeListLinkedTo';
import { ROUTES } from '@/lib/constants';

export function TasksPage() {
  const { projectId: projectIdParam } = useParams<{ projectId?: string }>();
  const parsedProjectId = projectIdParam ? parseInt(projectIdParam, 10) : undefined;
  const navigate = useNavigate();

  // Home node — drives org/project filtering and auto-redirect.
  const { data: whoami } = useWhoami();
  const homeNodeId = whoami?.homeNodeId ?? null;

  // Fetch projects linked to the home node (used for ProjectPillRow intersection
  // and auto-redirect). Disabled when homeNodeId is null/0.
  const { data: homeProjectsData } = useNodeListLinkedTo(
    homeNodeId ?? 0,
    { type: ['project'], count: 200 },
  );

  // Stable Set of home-project ids for intersection and redirect.
  // null = home node not yet resolved (don't filter / don't redirect yet).
  const homeProjectIds: Set<number> | null = useMemo(() => {
    if (homeNodeId == null) return null;
    if (!homeProjectsData) return null;
    return new Set(homeProjectsData.result.map((p) => p.id));
  }, [homeNodeId, homeProjectsData]);

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

  // Auto-redirect guard: fire at most once per mount.
  const hasRedirected = useRef<boolean>(false);

  useEffect(() => {
    // Already redirected this mount — don't fire again on re-renders.
    if (hasRedirected.current) return;
    // An explicit project URL is already in the path — honour it, don't override.
    if (parsedProjectId !== undefined) return;
    // Home node not yet resolved or not set — wait or skip.
    if (homeNodeId == null) return;
    // Home-projects query still in flight — wait.
    if (homeProjectIds === null) return;
    // No home-linked projects — nothing to redirect to.
    if (homeProjectIds.size === 0) return;

    // Navigate to the first home project (Set iteration order = insertion order
    // which matches the name-sorted API response).
    const [firstProjectId] = homeProjectIds;
    navigate(ROUTES.TASKS_PROJECT(firstProjectId), { replace: true });
    hasRedirected.current = true;
  }, [parsedProjectId, homeNodeId, homeProjectIds, navigate]);

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
          homeNodeId={homeNodeId}
        />
        <ProjectPillRow
          orgId={selectedOrgId ?? 0}
          selectedProjectId={orgMismatch ? undefined : parsedProjectId}
          homeProjectIds={homeProjectIds}
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
