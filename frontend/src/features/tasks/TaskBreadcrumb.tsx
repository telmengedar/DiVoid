/**
 * TaskBreadcrumb — lightweight breadcrumb for the task drill-down views.
 *
 * Shows: Tasks [/ Org name [/ Project name]]
 *
 * Names are resolved via useNode when the corresponding id prop is present.
 * Falls back to "..." while loading and hides gracefully on error.
 */

import { Link } from 'react-router-dom';
import { useNode } from '@/features/nodes/useNode';
import { ROUTES } from '@/lib/constants';

interface TaskBreadcrumbProps {
  orgId?: number;
  projectId?: number;
}

function Separator() {
  return <span className="text-muted-foreground" aria-hidden="true"> › </span>;
}

export function TaskBreadcrumb({ orgId, projectId }: TaskBreadcrumbProps) {
  const orgQuery = useNode(orgId ?? 0);
  const projectQuery = useNode(projectId ?? 0);

  return (
    <nav aria-label="Breadcrumb" className="text-sm text-muted-foreground">
      {orgId === undefined && projectId === undefined ? (
        <span className="font-medium text-foreground">Tasks</span>
      ) : (
        <>
          <Link to={ROUTES.TASKS} className="hover:text-foreground transition-colors">
            Tasks
          </Link>
          {orgId !== undefined && (
            <>
              <Separator />
              {projectId !== undefined ? (
                <Link
                  to={ROUTES.TASKS_ORG(orgId)}
                  className="hover:text-foreground transition-colors"
                >
                  {orgQuery.data?.name ?? (orgQuery.isLoading ? '…' : String(orgId))}
                </Link>
              ) : (
                <span className="font-medium text-foreground">
                  {orgQuery.data?.name ?? (orgQuery.isLoading ? '…' : String(orgId))}
                </span>
              )}
            </>
          )}
          {projectId !== undefined && (
            <>
              <Separator />
              <span className="font-medium text-foreground">
                {projectQuery.data?.name ?? (projectQuery.isLoading ? '…' : String(projectId))}
              </span>
            </>
          )}
        </>
      )}
    </nav>
  );
}
