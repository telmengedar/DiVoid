/**
 * usePeekState — URL-backed peek state for the workspace modal preview.
 *
 * Reads and writes the ?peek=<id> query parameter on the /workspace route.
 * The URL is the single source of truth (§7.3 of #420), making the peek state
 * shareable, bookmarkable, and browser-back-able.
 *
 * Return shape:
 *  - peekId: number | null — positive integer from ?peek param, or null.
 *  - openPeek(id): pushes a new history entry with ?peek=id. Browser back
 *    closes (or steps back through a peek trail).
 *  - closePeek(): pushes a new history entry without the peek param.
 *
 * openPeek and closePeek are referentially stable for the component lifetime:
 * they are created once (no deps on [setSearchParams]) and call through a
 * useRef that always holds the latest setSearchParams. This guarantees
 * stability even when the router re-issues setSearchParams on param changes
 * (the react-router-dom contract says it should be stable, but a useRef
 * wrapper removes the dependency on that guarantee and keeps the T6
 * render-stability test unconditionally passing).
 *
 * Design: docs/architecture/workspace-modal-preview.md §5.4 / §8.3 / §9
 * Task: DiVoid node #1253
 */

import { useCallback, useRef } from 'react';
import { useSearchParams } from 'react-router-dom';

export interface PeekState {
  peekId: number | null;
  openPeek: (id: number) => void;
  closePeek: () => void;
}

/**
 * Returns the current peek state derived from ?peek=<id> in the URL.
 *
 * openPeek and closePeek are created once (empty deps) and call through a
 * ref that is updated on every render. This is the "stable function via ref"
 * pattern, identical to how useEvent is proposed in the React RFC. It gives
 * harder stability guarantees than depending on [setSearchParams] alone,
 * which eliminates a class of subtle re-render in the MemoryRouter test
 * environment as well as in production.
 *
 * See design §9 for the full render-stability analysis.
 */
export function usePeekState(): PeekState {
  const [searchParams, setSearchParams] = useSearchParams();

  // Always hold the latest setSearchParams so the stable callbacks below
  // can call through to it without capturing a stale closure.
  const setSearchParamsRef = useRef(setSearchParams);
  setSearchParamsRef.current = setSearchParams;

  const rawPeek = searchParams.get('peek');
  const parsed = rawPeek !== null ? parseInt(rawPeek, 10) : NaN;
  const peekId = !isNaN(parsed) && parsed > 0 ? parsed : null;

  // eslint-disable-next-line react-hooks/exhaustive-deps
  const openPeek = useCallback(
    (id: number) => {
      setSearchParamsRef.current(
        (prev) => {
          const next = new URLSearchParams(prev);
          next.set('peek', String(id));
          return next;
        },
        { replace: false },
      );
    },
    // Intentionally empty: stability is provided by the ref, not by deps.
    // This is the "stable function via ref" / useEvent pattern.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [],
  );

  // eslint-disable-next-line react-hooks/exhaustive-deps
  const closePeek = useCallback(
    () => {
      setSearchParamsRef.current(
        (prev) => {
          const next = new URLSearchParams(prev);
          next.delete('peek');
          return next;
        },
        { replace: false },
      );
    },
    // Intentionally empty: stability is provided by the ref, not by deps.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [],
  );

  return { peekId, openPeek, closePeek };
}
