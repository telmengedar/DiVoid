/**
 * WorkspacePage — /workspace route entry point.
 *
 * Thin route shell. Owns the error boundary scoped to the workspace canvas.
 * All data fetching, graph rendering, and interaction handling live in
 * WorkspaceCanvas so this file stays as light as possible.
 *
 * The WorkspaceCanvas import is lazy at the route level (see routes.tsx). This
 * file is also lazy-loaded, so the @xyflow/react bundle is split from the main
 * chunk.
 *
 * Design: docs/architecture/workspace-mode.md §5.6
 * Task: DiVoid node #230
 */

import { Component, type ReactNode } from 'react';
import { WorkspaceCanvas } from './WorkspaceCanvas';

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

// ─── Page ──────────────────────────────────────────────────────────────────────

export function WorkspacePage() {
  return (
    <WorkspaceErrorBoundary>
      {/* Fill the full available height from AppShell */}
      <div className="h-full w-full">
        <WorkspaceCanvas />
      </div>
    </WorkspaceErrorBoundary>
  );
}
