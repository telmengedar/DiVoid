/**
 * WorkspaceSearchBar — floating in-canvas search affordance for the workspace.
 *
 * Mounts as a sibling of WorkspaceCanvas inside WorkspacePage (NOT as a child
 * of the canvas). This preserves the render-stability invariant established in
 * DiVoid #271: the canvas props never change when the search bar's local state
 * changes.
 *
 * ## Input modes (sniffed, per design §5.5 / §8.3)
 *
 *  - id-mode:   pure-digits input → Enter calls `onOpenPeek(id)` directly.
 *  - semantic:  any other non-empty input → debounced 250 ms →
 *               `useNodeSemantic` → dropdown of up to 8 ranked rows →
 *               row click calls `onOpenPeek(row.id)` and clears input.
 *  - empty:     no input → no fetch, no dropdown.
 *
 * ESC clears the input and dismisses any open dropdown.
 * Outside-click dismisses the dropdown (standard popover UX).
 *
 * Design: docs/architecture/open-by-id.md §5.3 / §6.2 / §6.3 / §8.2 / §8.3
 * Task: DiVoid #1607
 * Render-stability constraint: DiVoid #271
 * See also: usePeekState.ts, WorkspaceNodePeekModal.tsx
 */

import { useState, useRef, useEffect, useCallback } from 'react';
import { Search } from 'lucide-react';
import { cn } from '@/lib/cn';
import { useNodeSemantic } from '@/features/nodes/useNodeSemantic';
import type { NodeDetails } from '@/types/divoid';

const DEBOUNCE_MS = 250;

/**
 * Classifies a raw input string into one of three modes.
 *
 *  - `'empty'`  — blank or whitespace-only.
 *  - `'id'`     — trimmed value is purely digits and parses to a positive integer.
 *  - `'query'`  — anything else (non-digit characters, or `"0"`).
 *
 * Pure function; no side effects. Exported so the WT3 unit test can pin the
 * contract without rendering the full bar (load-bearing per DiVoid #275).
 *
 * Design §8.3 classifier contract:
 *   classifyInput('1462') → 'id'
 *   classifyInput('auth flow') → 'query'
 *   classifyInput('  ') → 'empty'
 */
export function classifyInput(raw: string): 'id' | 'query' | 'empty' {
  const trimmed = raw.trim();
  if (trimmed.length === 0) return 'empty';
  if (/^\d+$/.test(trimmed) && parseInt(trimmed, 10) > 0) return 'id';
  return 'query';
}

interface WorkspaceSearchBarProps {
  /**
   * Stable callback from `usePeekState.openPeek` (via WorkspacePage).
   * Called when the user selects a result — opens the peek modal for that id.
   * Must be referentially stable across renders so it does not cause the
   * search bar to re-render on peek-state changes. `usePeekState.openPeek`
   * satisfies this: it is created once with an empty dep array and reads
   * `setSearchParams` through a ref (see usePeekState.ts:60-75).
   */
  onOpenPeek: (id: number) => void;
}

/**
 * Floating in-canvas search bar positioned in the top-right of the canvas
 * overlay. Sibling of WorkspaceCanvas — never a child.
 *
 * @param onOpenPeek - stable callback from usePeekState.openPeek
 */
export function WorkspaceSearchBar({ onOpenPeek }: WorkspaceSearchBarProps) {
  const [input, setInput]               = useState('');
  const [debouncedQuery, setDebouncedQuery] = useState('');
  const [dropdownOpen, setDropdownOpen] = useState(false);

  const debounceRef  = useRef<ReturnType<typeof setTimeout> | null>(null);
  const containerRef = useRef<HTMLDivElement>(null);

  const mode = classifyInput(input);

  useEffect(() => {
    if (mode !== 'query') {
      setDebouncedQuery('');
      return;
    }
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => {
      setDebouncedQuery(input.trim());
    }, DEBOUNCE_MS);

    return () => {
      if (debounceRef.current) clearTimeout(debounceRef.current);
    };
  }, [input, mode]);

  const { data: semanticPage } = useNodeSemantic(debouncedQuery, { count: 8 });
  const semanticRows: NodeDetails[] = semanticPage?.result ?? [];

  useEffect(() => {
    if (semanticRows.length > 0 && mode === 'query') {
      setDropdownOpen(true);
    } else if (mode !== 'query') {
      setDropdownOpen(false);
    }
  }, [semanticRows, mode]);

  useEffect(() => {
    function handlePointerDown(e: PointerEvent) {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
        setDropdownOpen(false);
      }
    }
    document.addEventListener('pointerdown', handlePointerDown);
    return () => {
      document.removeEventListener('pointerdown', handlePointerDown);
    };
  }, []);

  const handleInputChange = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    setInput(e.target.value);
    if (e.target.value.trim().length === 0) {
      setDropdownOpen(false);
      setDebouncedQuery('');
    }
  }, []);

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLInputElement>) => {
      if (e.key === 'Escape') {
        setInput('');
        setDebouncedQuery('');
        setDropdownOpen(false);
        return;
      }
      if (e.key === 'Enter') {
        if (mode === 'id') {
          onOpenPeek(parseInt(input.trim(), 10));
          setInput('');
          setDropdownOpen(false);
        }
      }
    },
    [mode, input, onOpenPeek],
  );

  const handleRowClick = useCallback(
    (id: number) => {
      onOpenPeek(id);
      setInput('');
      setDebouncedQuery('');
      setDropdownOpen(false);
    },
    [onOpenPeek],
  );

  return (
    <div
      ref={containerRef}
      className="pointer-events-none absolute top-3 right-3 z-20 flex flex-col items-end"
      data-testid="workspace-search-bar"
    >
      <div className="pointer-events-auto flex flex-col">
        <div className="relative flex items-center">
          <Search
            size={14}
            aria-hidden="true"
            className="pointer-events-none absolute left-2.5 text-muted-foreground"
          />
          <input
            type="text"
            value={input}
            onChange={handleInputChange}
            onKeyDown={handleKeyDown}
            placeholder={mode === 'id' ? 'Press Enter to open…' : 'ID or search…'}
            aria-label="Search by ID or query"
            aria-haspopup="listbox"
            aria-expanded={dropdownOpen}
            className={cn(
              'h-8 w-52 rounded-md border border-border bg-background pl-8 pr-3 text-sm',
              'placeholder:text-muted-foreground',
              'focus:outline-none focus:ring-2 focus:ring-ring',
              'dark:bg-background/90',
            )}
          />
        </div>

        {dropdownOpen && semanticRows.length > 0 && (
          <ul
            role="listbox"
            aria-label="Search results"
            className={cn(
              'mt-1 w-52 rounded-md border border-border bg-popover shadow-md',
              'max-h-64 overflow-y-auto',
              'text-sm text-popover-foreground',
            )}
          >
            {semanticRows.map((row) => (
              <li
                key={row.id}
                role="option"
                aria-selected={false}
                onPointerDown={(e) => {
                  e.preventDefault();
                  handleRowClick(row.id);
                }}
                className={cn(
                  'flex cursor-pointer items-center justify-between gap-2',
                  'px-3 py-2 hover:bg-muted transition-colors',
                )}
              >
                <span className="truncate">{row.name}</span>
                {row.type && (
                  <span className="shrink-0 text-[10px] text-muted-foreground">{row.type}</span>
                )}
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}
