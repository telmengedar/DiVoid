/**
 * useNodeListLinkedTo — one-hop neighbour walk hook.
 *
 * Wraps GET /api/nodes?linkedto=<id>&... — the workhorse for fetching
 * all nodes linked to a known anchor node, optionally filtered by type,
 * status, name, etc.
 *
 * Design: docs/architecture/frontend-bootstrap.md §5.5
 * API reference: DiVoid node #8
 */

import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useAuth } from 'react-oidc-context';
import { useApiClient } from '@/lib/useApiClient';
import { API } from '@/lib/constants';
import type { NodeFilter, Page, NodeDetails } from '@/types/divoid';

export function nodeLinkedToQueryKey(linkedToId: number, filter?: Omit<NodeFilter, 'linkedto'>) {
  return ['nodes', 'linkedto', linkedToId, filter ?? {}] as const;
}

/**
 * Returns all nodes linked to `linkedToId`, with optional additional filters.
 * Disabled when `linkedToId` is 0 or falsy.
 */
export function useNodeListLinkedTo(
  linkedToId: number,
  filter?: Omit<NodeFilter, 'linkedto'>,
) {
  const auth = useAuth();
  const client = useApiClient();

  // Stabilise params so the queryFn closure never changes identity between
  // renders when the logical filter is the same.  Without this, useMemo-on-empty-
  // deps in useApiClient stays stable, but the arrow function capturing `params`
  // is still a new reference every render, which makes TanStack Query's
  // observer.setOptions() detect an options change on every render (shallow
  // equality of defaultedOptions fails on the queryFn reference).
  const params: NodeFilter = useMemo(
    () => ({ ...filter, linkedto: [linkedToId] }),
    // JSON-serialise the filter so the memo only recomputes when the logical
    // content changes, not when the caller passes a new object literal each render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [linkedToId, JSON.stringify(filter)],
  );

  return useQuery<Page<NodeDetails>>({
    queryKey: nodeLinkedToQueryKey(linkedToId, filter),
    queryFn: ({ signal }) =>
      client.get<Page<NodeDetails>>(API.NODES.LIST, params as Record<string, unknown>, signal),
    enabled: auth.isAuthenticated && linkedToId > 0,
    staleTime: 30_000,
  });
}
