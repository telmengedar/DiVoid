/**
 * WikiPage — wiki entry-redirect component at `/wiki`.
 *
 * Navigation logic:
 *  1. While whoami is loading — render nothing (race-guard).
 *  2. whoami resolved with homeNodeId set → redirect to /wiki/:homeNodeId.
 *  3. homeNodeId null → fetch first node via useNodeList({count:1, sort:'id'}).
 *     a. First node found → redirect to /wiki/:firstNodeId.
 *     b. No nodes → render empty state "Graph is empty — create a node…".
 *
 * Race-guard mirrors TasksPage.tsx:82-101: `useRef<boolean>(false)` prevents
 * a second redirect on re-render after the first Navigate fires.
 *
 * Task: DiVoid node #413
 */

import { useRef, useEffect } from 'react';
import { Navigate, useNavigate } from 'react-router-dom';
import { useWhoami } from '@/features/auth/useWhoami';
import { useNodeList } from '@/features/nodes/useNodeList';
import { ROUTES } from '@/lib/constants';

export function WikiPage() {
  const { data: whoami, isLoading: whoamiLoading } = useWhoami();
  const homeNodeId = whoami?.homeNodeId ?? null;

  // Only needed when homeNodeId is null — fallback to first node.
  const { data: firstNodePage } = useNodeList(
    homeNodeId === null && !whoamiLoading ? { count: 1, sort: 'id' } : undefined,
  );

  const hasRedirected = useRef<boolean>(false);
  const navigate = useNavigate();

  // Imperative redirect path for the fallback case (no home node, but nodes exist).
  useEffect(() => {
    if (hasRedirected.current) return;
    if (whoamiLoading) return;
    if (homeNodeId !== null) return; // handled by declarative Navigate below
    if (!firstNodePage) return; // still loading

    const firstNode = firstNodePage.result[0];
    if (!firstNode) return; // empty graph — render empty state

    navigate(ROUTES.WIKI_NODE(firstNode.id), { replace: true });
    hasRedirected.current = true;
  }, [whoamiLoading, homeNodeId, firstNodePage, navigate]);

  // Still loading whoami — render nothing.
  if (whoamiLoading) {
    return null;
  }

  // Home node is set — declarative redirect (immediate, no effect needed).
  if (homeNodeId !== null) {
    return <Navigate to={ROUTES.WIKI_NODE(homeNodeId)} replace />;
  }

  // No home node — check first node page.
  // If firstNodePage is still loading: render nothing (effect will redirect once ready).
  if (!firstNodePage) {
    return null;
  }

  // No nodes at all — empty state.
  if (firstNodePage.result.length === 0) {
    return (
      <div className="flex flex-col items-center justify-center h-full gap-3 text-center px-4">
        <p className="text-base font-medium">Graph is empty</p>
        <p className="text-sm text-muted-foreground">
          Create a node in the workspace first, then return here.
        </p>
      </div>
    );
  }

  // First node exists — effect is navigating; render nothing while it fires.
  return null;
}
