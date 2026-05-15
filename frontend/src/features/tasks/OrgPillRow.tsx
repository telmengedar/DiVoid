/**
 * OrgPillRow — one pill per organisation.
 *
 * Data: useNodeList({ type: ['organization'], sort: 'name' }).
 * Default-selected = the org linked to the current project (via useProjectOrg).
 *
 * Clicking a pill calls onOrgSelect — it does NOT navigate (explicit click
 * required on the project pill for navigation — see §3 of the contract).
 *
 * If the current project has no org link, renders a small inline topology note
 * instead of crashing.
 *
 * Task: DiVoid node #391
 */

import { useNodeList } from '@/features/nodes/useNodeList';
import type { NodeDetails } from '@/types/divoid';

interface OrgPillRowProps {
  /** The org id that should appear selected. */
  selectedOrgId: number | undefined;
  /** Called when the user clicks an org pill. */
  onOrgSelect: (orgId: number) => void;
  /** Render an inline topology warning if the project's org is unknown. */
  showTopologyWarning?: boolean;
}

export function OrgPillRow({ selectedOrgId, onOrgSelect, showTopologyWarning }: OrgPillRowProps) {
  const { data, isLoading, isError } = useNodeList({
    type: ['organization'],
    sort: 'name',
    count: 100,
  });

  if (isError) {
    return (
      <div
        role="alert"
        className="text-xs text-destructive"
      >
        Failed to load organisations.
      </div>
    );
  }

  const orgs: NodeDetails[] = data?.result ?? [];

  return (
    <div className="flex flex-col gap-1">
      <div
        className="flex flex-wrap gap-2"
        role="group"
        aria-label="Organisation filter"
        data-testid="org-pill-row"
      >
        {isLoading && (
          <span className="text-xs text-muted-foreground">Loading…</span>
        )}
        {orgs.map((org) => {
          const isSelected = org.id === selectedOrgId;
          return (
            <button
              key={org.id}
              type="button"
              onClick={() => onOrgSelect(org.id)}
              aria-pressed={isSelected}
              data-testid={`org-pill-${org.id}`}
              className={[
                'inline-flex items-center rounded-full px-3 py-1 text-xs font-medium border transition-colors',
                isSelected
                  ? 'bg-primary text-primary-foreground border-primary'
                  : 'bg-background text-muted-foreground border-border hover:border-foreground/40',
              ].join(' ')}
            >
              {org.name}
            </button>
          );
        })}
      </div>

      {showTopologyWarning && (
        <p
          className="text-xs text-muted-foreground"
          data-testid="org-unknown-warning"
        >
          This project&apos;s org is unknown — fix topology.
        </p>
      )}
    </div>
  );
}
