/**
 * WorkspacePage — /workspace route entry point.
 *
 * Owns the peek state (via usePeekState) and mounts the peek modal as a sibling
 * of WorkspaceCanvas. Peek-state changes re-render WorkspacePage and the modal;
 * WorkspaceCanvas is NOT affected because it only receives the stable onPeek
 * callback (see design §9 render-stability demonstration).
 *
 * The WorkspaceCanvas import is lazy at the route level (see routes.tsx). This
 * file is also lazy-loaded, so the @xyflow/react bundle is split from the main
 * chunk.
 *
 * Design: docs/architecture/workspace-modal-preview.md §5.4
 * Task: DiVoid node #230 / #1253
 */

import { Component, type ReactNode } from 'react';
import { WorkspaceCanvas } from './WorkspaceCanvas';
import { WorkspaceNodePeekModal } from './WorkspaceNodePeekModal';
import { usePeekState } from './usePeekState';

// ─── Error boundary ────────────────────────────────────────────────────────────

interface ErrorBoundaryState {
  error: Error | null;
}

class WorkspaceErrorBoundary extends Component<
  { children: ReactNode },
  ErrorBoundaryState
> {
  constructor(props: { children: ReactNode }) {
    super(props);
    this.state = { error: null };
  }

  static getDerivedStateFromError(error: Error): ErrorBoundaryState {
    return { error };
  }

  override render() {
    if (this.state.error) {
      return (
        <div className="flex h-full items-center justify-center">
          <div className="text-center max-w-sm">
            <p className="font-medium text-destructive mb-2">Workspace failed to load</p>
            <p className="text-sm text-muted-foreground">{this.state.error.message}</p>
            <button
              onClick={() => this.setState({ error: null })}
              className="mt-4 h-9 px-4 rounded-md border border-border text-sm hover:bg-muted transition-colors"
            >
              Retry
            </button>
          </div>
        </div>
      );
    }

    return this.props.children;
  }
}

// ─── Inner page (needs hooks — must be a function component) ───────────────────

function WorkspacePageInner() {
  const { peekId, openPeek, closePeek } = usePeekState();

  return (
    <div className="h-full w-full">
      <WorkspaceCanvas onPeek={openPeek} />
      <WorkspaceNodePeekModal
        peekId={peekId}
        onClose={closePeek}
        onPeekChange={openPeek}
      />
    </div>
  );
}

// ─── Page ──────────────────────────────────────────────────────────────────────

export function WorkspacePage() {
  return (
    <WorkspaceErrorBoundary>
      <WorkspacePageInner />
    </WorkspaceErrorBoundary>
  );
}
