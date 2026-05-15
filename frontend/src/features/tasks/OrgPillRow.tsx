/**
 * OrgPillRow — one pill per organisation.
 *
 * Data source depends on `homeNodeId`:
 *  - When `homeNodeId` is a number: useNodeListLinkedTo(homeNodeId, { type: ['organization'], sort: 'name', count: 100 })
 *  - When null / undefined (no home set or still loading): falls back to useNodeList({ type: ['organization'], sort: 'name', count: 100 })
 *
 * Default-selected = the org linked to the current project (via useProjectOrg).
 *
 * Clicking a pill calls onOrgSelect — it does NOT navigate (explicit click
 * required on the project pill for navigation — see §3 of the contract).
 *
 * If the current project has no org link, renders a small inline topology note
 * instead of crashing.
 *
 * Task: DiVoid node #391, #400
 */

import { useNodeList } from '@/features/nodes/useNodeList';
import { useNodeListLinkedTo } from '@/features/nodes/useNodeListLinkedTo';
import type { NodeDetails } from '@/types/divoid';

interface OrgPillRowProps {
  /** The org id that should appear selected. */
  selectedOrgId: number | undefined;
  /** Called when the user clicks an org pill. */
  onOrgSelect: (orgId: number) => void;
  /** Render an inline topology warning if the project's org is unknown. */
  showTopologyWarning?: boolean;
  /**
   * When set to a number, only orgs linked to this home node are shown.
   * When null/undefined (loading or no home node set) falls back to full org list.
   */
  homeNodeId?: number | null;
}

const ORG_FILTER = { type: ['organization'] as string[], sort: 'name' as const, count: 100 };

/**
 * Inner component that uses the linkedto hook — only rendered when homeNodeId is a number.
 * Separated to keep hook call count consistent (Rules of Hooks).
 */
function OrgPillRowFiltered({
  homeNodeId,
  selectedOrgId,
  onOrgSelect,
  showTopologyWarning,
}: {
  homeNodeId: number;
  selectedOrgId: number | undefined;
  onOrgSelect: (orgId: number) => void;
  showTopologyWarning?: boolean;
}) {
  const { data, isLoading, isError } = useNodeListLinkedTo(homeNodeId, ORG_FILTER);
  return (
    <OrgPillRowInner
      orgs={data?.result ?? []}
      isLoading={isLoading}
      isError={isError}
      selectedOrgId={selectedOrgId}
      onOrgSelect={onOrgSelect}
      showTopologyWarning={showTopologyWarning}
    />
  );
}

function OrgPillRowUnfiltered({
  selectedOrgId,
  onOrgSelect,
  showTopologyWarning,
}: {
  selectedOrgId: number | undefined;
  onOrgSelect: (orgId: number) => void;
  showTopologyWarning?: boolean;
}) {
  const { data, isLoading, isError } = useNodeList(ORG_FILTER);
  return (
    <OrgPillRowInner
      orgs={data?.result ?? []}
      isLoading={isLoading}
      isError={isError}
      selectedOrgId={selectedOrgId}
      onOrgSelect={onOrgSelect}
      showTopologyWarning={showTopologyWarning}
    />
  );
}

function OrgPillRowInner({
  orgs,
  isLoading,
  isError,
  selectedOrgId,
  onOrgSelect,
  showTopologyWarning,
}: {
  orgs: NodeDetails[];
  isLoading: boolean;
  isError: boolean;
  selectedOrgId: number | undefined;
  onOrgSelect: (orgId: number) => void;
  showTopologyWarning?: boolean;
}) {
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

export function OrgPillRow({ selectedOrgId, onOrgSelect, showTopologyWarning, homeNodeId }: OrgPillRowProps) {
  if (typeof homeNodeId === 'number') {
    return (
      <OrgPillRowFiltered
        homeNodeId={homeNodeId}
        selectedOrgId={selectedOrgId}
        onOrgSelect={onOrgSelect}
        showTopologyWarning={showTopologyWarning}
      />
    );
  }

  return (
    <OrgPillRowUnfiltered
      selectedOrgId={selectedOrgId}
      onOrgSelect={onOrgSelect}
      showTopologyWarning={showTopologyWarning}
    />
  );
}
