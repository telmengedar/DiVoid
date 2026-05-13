/**
 * Tasks — drill-down list view: Org → Project → Task list.
 *
 * Three sub-views rendered based on which route params are present:
 *  - /tasks                  → OrgListView
 *  - /tasks/orgs/:orgId      → ProjectListView
 *  - /tasks/projects/:projectId → TaskListView
 *
 * PR 5 step 1. Kanban board, status filters, and add-task affordance are out of scope.
 */

import { useParams } from 'react-router-dom';
import { OrgListView } from './OrgListView';
import { ProjectListView } from './ProjectListView';
import { TaskListView } from './TaskListView';
import { TaskBreadcrumb } from './TaskBreadcrumb';

export function TasksPage() {
  const { orgId, projectId } = useParams<{ orgId?: string; projectId?: string }>();

  const parsedOrgId = orgId ? parseInt(orgId, 10) : undefined;
  const parsedProjectId = projectId ? parseInt(projectId, 10) : undefined;

  return (
    <div className="flex flex-col gap-4 p-4">
      <div className="flex flex-col gap-1">
        <h1 className="text-xl font-semibold">Tasks</h1>
        <TaskBreadcrumb orgId={parsedOrgId} projectId={parsedProjectId} />
      </div>

      {parsedProjectId !== undefined ? (
        <TaskListView projectId={parsedProjectId} />
      ) : parsedOrgId !== undefined ? (
        <ProjectListView orgId={parsedOrgId} />
      ) : (
        <OrgListView />
      )}
    </div>
  );
}
