/**
 * NodeCardRenderer — custom xyflow node renderer for workspace nodes.
 *
 * Renders a small card showing:
 *  - Type badge (pill, coloured by type)
 *  - Name (truncated)
 *  - Status pill (when present)
 *
 * This component is registered with xyflow as a custom node type and is called
 * for every visible node. It is memoised with React.memo + a stable prop-equality
 * function so canvas re-renders (from viewport changes) do not cascade into
 * every node's render.
 *
 * Click handler: navigate to /nodes/{id} (existing detail page). Rationale for
 * this choice over a side-panel preview is filed in DiVoid documentation node
 * (see WorkspacePage.tsx for the node reference). Double-click is reserved.
 *
 * Design: docs/architecture/workspace-mode.md §5.12
 * Task: DiVoid node #230
 */

import { memo } from 'react';
import { Handle, Position, type NodeProps } from '@xyflow/react';
import { useNavigate } from 'react-router-dom';
import { ROUTES } from '@/lib/constants';
import type { PositionedNodeDetails } from '@/types/divoid';
import { cn } from '@/lib/cn';

/** The data payload stored in each xyflow Node object. */
export type NodeCardData = PositionedNodeDetails;

/** Minimal set of props compared for re-render equality. */
function propsAreEqual(prev: NodeProps<NodeCardData>, next: NodeProps<NodeCardData>) {
  // Re-render only when the data content or selection state changes.
  return (
    prev.data.id === next.data.id &&
    prev.data.name === next.data.name &&
    prev.data.type === next.data.type &&
    prev.data.status === next.data.status &&
    prev.selected === next.selected
  );
}

/** Maps common type names to a background colour class for the badge. */
function typeColour(type: string): string {
  switch (type) {
    case 'task':        return 'bg-blue-500/20 text-blue-600 dark:text-blue-400';
    case 'bug':         return 'bg-red-500/20 text-red-600 dark:text-red-400';
    case 'project':     return 'bg-purple-500/20 text-purple-600 dark:text-purple-400';
    case 'documentation': return 'bg-amber-500/20 text-amber-600 dark:text-amber-400';
    case 'person':      return 'bg-green-500/20 text-green-600 dark:text-green-400';
    case 'organization': return 'bg-teal-500/20 text-teal-600 dark:text-teal-400';
    default:            return 'bg-muted text-muted-foreground';
  }
}

/** Maps status strings to a colour hint. */
function statusColour(status: string): string {
  switch (status) {
    case 'open':        return 'text-blue-500 dark:text-blue-400';
    case 'in-progress': return 'text-amber-500 dark:text-amber-400';
    case 'closed':      return 'text-green-500 dark:text-green-400';
    case 'new':         return 'text-muted-foreground';
    default:            return 'text-muted-foreground';
  }
}

function NodeCardRendererInner({ data, selected }: NodeProps<NodeCardData>) {
  const navigate = useNavigate();

  const handleClick = () => {
    navigate(ROUTES.NODE_DETAIL(data.id));
  };

  return (
    <div
      role="button"
      tabIndex={0}
      aria-label={`Node: ${data.name} (${data.type})`}
      onClick={handleClick}
      onKeyDown={(e) => {
        if (e.key === 'Enter' || e.key === ' ') {
          e.preventDefault();
          handleClick();
        }
      }}
      className={cn(
        'min-w-[140px] max-w-[200px] rounded-md border bg-background px-3 py-2 shadow-sm cursor-pointer',
        'transition-colors hover:bg-muted/50',
        selected
          ? 'border-primary ring-1 ring-primary'
          : 'border-border',
      )}
    >
      {/* Type badge */}
      <div className="flex items-center gap-1.5 mb-1">
        <span
          className={cn(
            'inline-block rounded px-1.5 py-0.5 text-[10px] font-medium leading-tight uppercase tracking-wide',
            typeColour(data.type),
          )}
        >
          {data.type}
        </span>
        {data.status && (
          <span className={cn('text-[10px] leading-tight', statusColour(data.status))}>
            {data.status}
          </span>
        )}
      </div>

      {/* Name */}
      <p className="text-sm font-medium leading-snug line-clamp-2 text-foreground">
        {data.name}
      </p>

      {/* xyflow connection handles — invisible in normal use */}
      <Handle
        type="source"
        position={Position.Right}
        className="!bg-border !border-border"
        aria-hidden="true"
      />
      <Handle
        type="target"
        position={Position.Left}
        className="!bg-border !border-border"
        aria-hidden="true"
      />
    </div>
  );
}

export const NodeCardRenderer = memo(NodeCardRendererInner, propsAreEqual);
NodeCardRenderer.displayName = 'NodeCardRenderer';
