/**
 * NodeDetailPage — /nodes/:id route shell.
 *
 * Thin wrapper: parses the route param, validates the id, renders the back
 * button, and delegates all content rendering to NodeDetailView.
 *
 * The back button uses sessionStorage('divoid.lastLocation') written by
 * LocationTracker in routes.tsx. See bug #388 for why window.history.state.idx
 * is not used (works in MemoryRouter tests, unreliable in BrowserRouter prod).
 *
 * onClose passed to NodeDetailView navigates to /search — this is the route
 * variant's "action implies close" behaviour (design §5.1.1). The modal variant
 * supplies its own onClose that calls closePeek() instead.
 *
 * Design: docs/architecture/workspace-modal-preview.md §5.2
 * Task: DiVoid node #229 / #1253
 */

import { useCallback } from 'react';
import { useParams, useNavigate, useLocation } from 'react-router-dom';
import { ChevronLeft } from 'lucide-react';
import { NodeDetailView } from './NodeDetailView';
import { ROUTES } from '@/lib/constants';

export function NodeDetailPage() {
  const { id: idParam } = useParams<{ id: string }>();
  const nodeId = idParam ? parseInt(idParam, 10) : 0;
  const navigate = useNavigate();
  const location = useLocation();

  /**
   * Navigate back to wherever the user came from.
   *
   * Detection: reads sessionStorage('divoid.lastLocation') written by
   * LocationTracker in routes.tsx. This is a plain sessionStorage read —
   * the same primitive in jsdom and the browser — unlike
   * window.history.state.idx which is a react-router-dom 7 internal counter
   * that is reliably populated under MemoryRouter (tests) but NOT under
   * BrowserRouter (production). PR #55's fix fell into that trap.
   *
   * Self-equality guard: if sessionStorage holds the current path (e.g. the
   * user navigated node→node and the last entry happens to be this node),
   * fall back to /search to avoid a self-navigation loop.
   *
   * Bug #388.
   */
  const handleBack = useCallback(() => {
    const last = sessionStorage.getItem('divoid.lastLocation');
    const currentPath = location.pathname + location.search;
    if (last && last !== currentPath) {
      navigate(last);
    } else {
      navigate(ROUTES.SEARCH);
    }
  }, [navigate, location.pathname, location.search]);

  /**
   * Route variant onClose: navigate to /search after Delete success.
   * This preserves the existing NodeDetailPage behaviour (design §5.1.1).
   */
  const handleClose = useCallback(() => {
    navigate(ROUTES.SEARCH);
  }, [navigate]);

  if (!idParam || isNaN(nodeId) || nodeId <= 0) {
    return (
      <div className="mx-auto max-w-5xl px-4 py-6">
        <p className="text-sm text-destructive" role="alert">
          Invalid node ID.
        </p>
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-5xl px-4 py-6 flex flex-col gap-6">
      {/* Back button — returns to previous history entry, or /search when none */}
      <button
        type="button"
        onClick={handleBack}
        className="inline-flex items-center gap-1 self-start text-sm text-muted-foreground hover:text-foreground transition-colors"
        aria-label="Go back"
        data-testid="back-button"
      >
        <ChevronLeft size={15} aria-hidden="true" />
        Back
      </button>

      <NodeDetailView nodeId={nodeId} onClose={handleClose} />
    </div>
  );
}
