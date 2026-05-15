/**
 * WikiSideNav — left side-nav for the wiki layout.
 *
 * Default state: shows nodes linked to the current `nodeId` via
 * `useNodeListLinkedTo`. Each row is a `<Link>` to `/wiki/:id`.
 *
 * Search state: when the debounced input is non-empty, replaces the
 * neighbour list with `useNodeSemantic` results IN THE SAME COLUMN.
 * A "× clear" affordance restores neighbours.
 *
 * Each row: name (line-clamp-1) + type chip + StatusBadge.
 *
 * Out of scope: pagination, term highlighting, modal/popover overlay (W2+).
 *
 * Task: DiVoid node #413
 */

import { useState, useEffect, useRef } from 'react';
import { Link } from 'react-router-dom';
import { X } from 'lucide-react';
import { useNodeListLinkedTo } from '@/features/nodes/useNodeListLinkedTo';
import { useNodeSemantic } from '@/features/nodes/useNodeSemantic';
import { StatusBadge } from '@/components/common/StatusBadge';
import { ROUTES } from '@/lib/constants';
import type { NodeDetails } from '@/types/divoid';

interface WikiSideNavProps {
  nodeId: number;
}

interface NavRowProps {
  node: NodeDetails;
}

function NavRow({ node }: NavRowProps) {
  return (
    <Link
      to={ROUTES.WIKI_NODE(node.id)}
      className="flex items-center gap-2 px-3 py-2 rounded-md hover:bg-muted transition-colors text-sm group"
      aria-label={`Navigate to ${node.name ?? `Node ${node.id}`}`}
    >
      <span className="flex-1 truncate font-medium text-foreground group-hover:text-primary transition-colors line-clamp-1">
        {node.name ?? `Node ${node.id}`}
      </span>
      {node.type && (
        <span className="shrink-0 rounded bg-muted px-1.5 py-0.5 text-xs font-mono text-muted-foreground">
          {node.type}
        </span>
      )}
      <span className="shrink-0">
        <StatusBadge status={node.status} />
      </span>
    </Link>
  );
}

export function WikiSideNav({ nodeId }: WikiSideNavProps) {
  const [inputValue, setInputValue] = useState('');
  const [debouncedQuery, setDebouncedQuery] = useState('');
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Debounce the search input (~250 ms).
  useEffect(() => {
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => {
      setDebouncedQuery(inputValue.trim());
    }, 250);
    return () => {
      if (debounceRef.current) clearTimeout(debounceRef.current);
    };
  }, [inputValue]);

  const isSearching = debouncedQuery.length > 0;

  // Linked neighbours (default state).
  const { data: neighboursData, isFetching: neighboursFetching } = useNodeListLinkedTo(
    nodeId,
    { count: 200 },
  );

  // Semantic search results (search state).
  const { data: searchData, isFetching: searchFetching } = useNodeSemantic(debouncedQuery);

  const neighbours = neighboursData?.result ?? [];
  const searchResults = searchData?.result ?? [];

  const isFetching = isSearching ? searchFetching : neighboursFetching;
  const rows = isSearching ? searchResults : neighbours;

  function handleClear() {
    setInputValue('');
    setDebouncedQuery('');
  }

  return (
    <aside className="flex flex-col h-full" aria-label="Wiki side navigation">
      {/* Search input */}
      <div className="px-3 py-3 border-b border-border">
        <div className="relative">
          <input
            type="search"
            value={inputValue}
            onChange={(e) => setInputValue(e.target.value)}
            placeholder="Search nodes…"
            aria-label="Search nodes"
            className="w-full h-8 rounded-md border border-border bg-background px-3 pr-8 text-sm placeholder:text-muted-foreground focus:outline-none focus:ring-2 focus:ring-ring"
          />
          {inputValue && (
            <button
              type="button"
              onClick={handleClear}
              aria-label="Clear search"
              className="absolute right-2 top-1/2 -translate-y-1/2 text-muted-foreground hover:text-foreground transition-colors"
            >
              <X size={14} aria-hidden="true" />
            </button>
          )}
        </div>
        {isSearching && (
          <p className="mt-1.5 text-xs text-muted-foreground">
            Search results for "{debouncedQuery}"
          </p>
        )}
      </div>

      {/* Node list */}
      <nav
        className="flex-1 overflow-y-auto py-1"
        aria-label={isSearching ? 'Search results' : 'Linked nodes'}
      >
        {isFetching && rows.length === 0 ? (
          <div className="px-3 py-2 space-y-1">
            {[...Array(5)].map((_, i) => (
              <div key={i} className="h-8 rounded bg-muted animate-pulse" />
            ))}
          </div>
        ) : rows.length === 0 ? (
          <p className="px-3 py-4 text-sm text-muted-foreground italic">
            {isSearching ? 'No results.' : 'No linked nodes.'}
          </p>
        ) : (
          rows.map((node) => <NavRow key={node.id} node={node} />)
        )}
      </nav>
    </aside>
  );
}
