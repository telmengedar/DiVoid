/**
 * SearchPage — /search route.
 *
 * Three-tab search surface for the three DiVoid retrieval modes:
 *  1. Semantic — plain-language query ranked by vector similarity.
 *  2. Linked  — one-hop neighbour walk from a known node id.
 *  3. Path    — graph path traversal using the path expression grammar.
 *
 * Read-only. No create/edit/delete buttons. All data comes from the backend.
 *
 * Design: docs/architecture/frontend-bootstrap.md §5.5, §6
 * Task: DiVoid node #228
 */

import { useState, useCallback } from 'react';
import { toast } from 'sonner';
import { Search, Link2, GitBranch } from 'lucide-react';
import { useNodeSemantic } from './useNodeSemantic';
import { useNodeListLinkedTo } from './useNodeListLinkedTo';
import { useNodePath } from './useNodePath';
import { NodeResultTable } from '@/components/common/NodeResultTable';
import { DivoidApiError } from '@/types/divoid';
import { cn } from '@/lib/cn';

// ─── Types ────────────────────────────────────────────────────────────────────

type Tab = 'semantic' | 'linked' | 'path';

// ─── Tab bar ──────────────────────────────────────────────────────────────────

interface TabBarProps {
  active: Tab;
  onChange: (tab: Tab) => void;
}

const TABS: { id: Tab; label: string; Icon: React.ElementType }[] = [
  { id: 'semantic', label: 'Semantic', Icon: Search },
  { id: 'linked', label: 'Linked', Icon: Link2 },
  { id: 'path', label: 'Path', Icon: GitBranch },
];

function TabBar({ active, onChange }: TabBarProps) {
  return (
    <div role="tablist" aria-label="Search mode" className="flex gap-1 border-b border-border">
      {TABS.map(({ id, label, Icon }) => (
        <button
          key={id}
          role="tab"
          aria-selected={active === id}
          aria-controls={`panel-${id}`}
          id={`tab-${id}`}
          onClick={() => onChange(id)}
          className={cn(
            'inline-flex items-center gap-1.5 px-4 py-2.5 text-sm font-medium border-b-2 -mb-px transition-colors',
            active === id
              ? 'border-primary text-foreground'
              : 'border-transparent text-muted-foreground hover:text-foreground hover:border-border',
          )}
        >
          <Icon size={15} aria-hidden="true" />
          {label}
        </button>
      ))}
    </div>
  );
}

// ─── Filter bar (type + status) ───────────────────────────────────────────────

interface FilterBarProps {
  typeFilter: string;
  statusFilter: string;
  onTypeChange: (v: string) => void;
  onStatusChange: (v: string) => void;
}

function FilterBar({ typeFilter, statusFilter, onTypeChange, onStatusChange }: FilterBarProps) {
  return (
    <div className="flex flex-wrap gap-3 items-end">
      <div className="flex flex-col gap-1">
        <label htmlFor="filter-type" className="text-xs font-medium text-muted-foreground">
          Type
        </label>
        <input
          id="filter-type"
          type="text"
          placeholder="task, documentation…"
          value={typeFilter}
          onChange={(e) => onTypeChange(e.target.value)}
          className="h-8 rounded-md border border-border bg-background px-3 text-sm placeholder:text-muted-foreground focus:outline-none focus:ring-2 focus:ring-ring w-44"
          aria-label="Filter by type"
        />
      </div>
      <div className="flex flex-col gap-1">
        <label htmlFor="filter-status" className="text-xs font-medium text-muted-foreground">
          Status
        </label>
        <input
          id="filter-status"
          type="text"
          placeholder="open, closed…"
          value={statusFilter}
          onChange={(e) => onStatusChange(e.target.value)}
          className="h-8 rounded-md border border-border bg-background px-3 text-sm placeholder:text-muted-foreground focus:outline-none focus:ring-2 focus:ring-ring w-36"
          aria-label="Filter by status"
        />
      </div>
    </div>
  );
}

// ─── Semantic tab panel ───────────────────────────────────────────────────────

function SemanticPanel() {
  const [input, setInput] = useState('');
  const [query, setQuery] = useState('');
  const [typeFilter, setTypeFilter] = useState('');
  const [statusFilter, setStatusFilter] = useState('');

  const filter = {
    type: typeFilter ? typeFilter.split(',').map((s) => s.trim()).filter(Boolean) : undefined,
    status: statusFilter ? statusFilter.split(',').map((s) => s.trim()).filter(Boolean) : undefined,
    count: 50,
  };

  const { data, isFetching, error } = useNodeSemantic(query, filter);

  if (error instanceof DivoidApiError) {
    toast.error(`${error.code}: ${error.text}`);
  }

  const handleSubmit = useCallback(
    (e: React.FormEvent) => {
      e.preventDefault();
      setQuery(input.trim());
    },
    [input],
  );

  return (
    <div
      id="panel-semantic"
      role="tabpanel"
      aria-labelledby="tab-semantic"
      className="flex flex-col gap-4"
    >
      <form onSubmit={handleSubmit} className="flex gap-2">
        <input
          type="search"
          value={input}
          onChange={(e) => setInput(e.target.value)}
          placeholder="Ask a question or describe what you're looking for…"
          aria-label="Semantic search query"
          className="flex-1 h-9 rounded-md border border-border bg-background px-3 text-sm placeholder:text-muted-foreground focus:outline-none focus:ring-2 focus:ring-ring"
        />
        <button
          type="submit"
          disabled={!input.trim()}
          className="inline-flex items-center gap-1.5 h-9 px-4 rounded-md bg-primary text-primary-foreground text-sm font-medium hover:opacity-90 disabled:opacity-50 disabled:cursor-not-allowed transition-opacity"
        >
          <Search size={14} aria-hidden="true" />
          Search
        </button>
      </form>

      <FilterBar
        typeFilter={typeFilter}
        statusFilter={statusFilter}
        onTypeChange={setTypeFilter}
        onStatusChange={setStatusFilter}
      />

      {error && !(error instanceof DivoidApiError) && (
        <p className="text-sm text-destructive" role="alert">
          Search failed. Please try again.
        </p>
      )}

      <NodeResultTable nodes={data?.result ?? []} loading={isFetching && query.length > 0} />

      {data && data.total >= 0 && (
        <p className="text-xs text-muted-foreground">
          {data.total} result{data.total !== 1 ? 's' : ''} total
        </p>
      )}
    </div>
  );
}

// ─── Linked tab panel ─────────────────────────────────────────────────────────

function LinkedPanel() {
  const [inputId, setInputId] = useState('');
  const [anchorId, setAnchorId] = useState(0);
  const [typeFilter, setTypeFilter] = useState('');
  const [statusFilter, setStatusFilter] = useState('');

  const filter = {
    type: typeFilter ? typeFilter.split(',').map((s) => s.trim()).filter(Boolean) : undefined,
    status: statusFilter ? statusFilter.split(',').map((s) => s.trim()).filter(Boolean) : undefined,
    count: 100,
  };

  const { data, isFetching, error } = useNodeListLinkedTo(anchorId, filter);

  if (error instanceof DivoidApiError) {
    toast.error(`${error.code}: ${error.text}`);
  }

  const handleSubmit = useCallback(
    (e: React.FormEvent) => {
      e.preventDefault();
      const parsed = parseInt(inputId.trim(), 10);
      if (!isNaN(parsed) && parsed > 0) {
        setAnchorId(parsed);
      }
    },
    [inputId],
  );

  return (
    <div
      id="panel-linked"
      role="tabpanel"
      aria-labelledby="tab-linked"
      className="flex flex-col gap-4"
    >
      <form onSubmit={handleSubmit} className="flex gap-2">
        <input
          type="number"
          min={1}
          value={inputId}
          onChange={(e) => setInputId(e.target.value)}
          placeholder="Node ID to walk from…"
          aria-label="Anchor node ID"
          className="w-44 h-9 rounded-md border border-border bg-background px-3 text-sm placeholder:text-muted-foreground focus:outline-none focus:ring-2 focus:ring-ring"
        />
        <button
          type="submit"
          disabled={!inputId.trim() || isNaN(parseInt(inputId, 10))}
          className="inline-flex items-center gap-1.5 h-9 px-4 rounded-md bg-primary text-primary-foreground text-sm font-medium hover:opacity-90 disabled:opacity-50 disabled:cursor-not-allowed transition-opacity"
        >
          <Link2 size={14} aria-hidden="true" />
          Browse
        </button>
      </form>

      <FilterBar
        typeFilter={typeFilter}
        statusFilter={statusFilter}
        onTypeChange={setTypeFilter}
        onStatusChange={setStatusFilter}
      />

      {error && !(error instanceof DivoidApiError) && (
        <p className="text-sm text-destructive" role="alert">
          Failed to load neighbours. Please try again.
        </p>
      )}

      <NodeResultTable nodes={data?.result ?? []} loading={isFetching && anchorId > 0} />

      {data && data.total >= 0 && (
        <p className="text-xs text-muted-foreground">
          {data.total} neighbour{data.total !== 1 ? 's' : ''}
        </p>
      )}
    </div>
  );
}

// ─── Path tab panel ───────────────────────────────────────────────────────────

function PathPanel() {
  const [input, setInput] = useState('');
  const [path, setPath] = useState('');

  const { data, isFetching, error } = useNodePath(path);

  const pathError = error instanceof DivoidApiError ? error : null;

  if (pathError && pathError.status !== 400) {
    toast.error(`${pathError.code}: ${pathError.text}`);
  }

  const handleSubmit = useCallback(
    (e: React.FormEvent) => {
      e.preventDefault();
      setPath(input.trim());
    },
    [input],
  );

  return (
    <div
      id="panel-path"
      role="tabpanel"
      aria-labelledby="tab-path"
      className="flex flex-col gap-4"
    >
      <form onSubmit={handleSubmit} className="flex gap-2">
        <input
          type="text"
          value={input}
          onChange={(e) => setInput(e.target.value)}
          placeholder="[type:project,name:DiVoid]/[type:task,status:open]"
          aria-label="Path expression"
          className="flex-1 h-9 rounded-md border border-border bg-background px-3 text-sm font-mono placeholder:text-muted-foreground placeholder:font-sans focus:outline-none focus:ring-2 focus:ring-ring"
          spellCheck={false}
        />
        <button
          type="submit"
          disabled={!input.trim()}
          className="inline-flex items-center gap-1.5 h-9 px-4 rounded-md bg-primary text-primary-foreground text-sm font-medium hover:opacity-90 disabled:opacity-50 disabled:cursor-not-allowed transition-opacity"
        >
          <GitBranch size={14} aria-hidden="true" />
          Traverse
        </button>
      </form>

      {/* 400 errors carry column-pointing syntax messages — show them inline */}
      {pathError && pathError.status === 400 && (
        <p
          className="text-sm text-destructive bg-destructive/10 rounded-md px-3 py-2 font-mono"
          role="alert"
        >
          {pathError.text}
        </p>
      )}

      {error && !pathError && (
        <p className="text-sm text-destructive" role="alert">
          Traversal failed. Please try again.
        </p>
      )}

      <NodeResultTable nodes={data?.result ?? []} loading={isFetching && path.length > 0} />

      {data && data.total >= 0 && (
        <p className="text-xs text-muted-foreground">
          {data.total} result{data.total !== 1 ? 's' : ''}
        </p>
      )}
    </div>
  );
}

// ─── Page ─────────────────────────────────────────────────────────────────────

export function SearchPage() {
  const [activeTab, setActiveTab] = useState<Tab>('semantic');

  return (
    <div className="mx-auto max-w-5xl px-4 py-6 flex flex-col gap-6">
      <div>
        <h1 className="text-xl font-semibold">Search</h1>
        <p className="text-sm text-muted-foreground mt-1">
          Three retrieval modes for the DiVoid graph.
        </p>
      </div>

      <div className="flex flex-col gap-4">
        <TabBar active={activeTab} onChange={setActiveTab} />

        <div className="pt-2">
          {activeTab === 'semantic' && <SemanticPanel />}
          {activeTab === 'linked' && <LinkedPanel />}
          {activeTab === 'path' && <PathPanel />}
        </div>
      </div>
    </div>
  );
}
