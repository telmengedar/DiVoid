/**
 * NodeDetailPage — /nodes/:id route.
 *
 * Shows four canonical regions:
 *  1. Metadata — id, type, name, status, contentType.
 *  2. Content blob — markdown rendered with rehype-sanitize; raw text fallback.
 *  3. Linked neighbours — via useNodeListLinkedTo (clicking through navigates).
 *  4. Error state — DivoidApiError.text via sonner toast; 404 shows inline.
 *
 * Read-only. No edit/delete/link buttons (PR 3).
 *
 * Design: docs/architecture/frontend-bootstrap.md §5.5, §9.3
 * Task: DiVoid node #228
 */

import { useEffect } from 'react';
import { useParams, Link } from 'react-router-dom';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeSanitize from 'rehype-sanitize';
import { toast } from 'sonner';
import { ChevronLeft } from 'lucide-react';
import { useNode } from './useNode';
import { useNodeContent } from './useNodeContent';
import { useNodeListLinkedTo } from './useNodeListLinkedTo';
import { NodeResultTable } from '@/components/common/NodeResultTable';
import { DivoidApiError } from '@/types/divoid';
import { ROUTES } from '@/lib/constants';

// ─── Metadata region ──────────────────────────────────────────────────────────

function MetadataRow({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div className="flex items-start gap-3 py-1.5 border-b border-border last:border-0">
      <span className="w-28 shrink-0 text-xs font-medium text-muted-foreground">{label}</span>
      <span className="text-sm">{value}</span>
    </div>
  );
}

function StatusBadge({ status }: { status: string | null }) {
  if (!status) return <span className="text-muted-foreground">—</span>;

  const colorMap: Record<string, string> = {
    open: 'bg-emerald-100 text-emerald-800 dark:bg-emerald-900/30 dark:text-emerald-400',
    'in-progress': 'bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-400',
    closed: 'bg-muted text-muted-foreground',
    new: 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-400',
    fixed: 'bg-purple-100 text-purple-800 dark:bg-purple-900/30 dark:text-purple-400',
  };

  const classes =
    colorMap[status] ?? 'bg-muted text-muted-foreground';

  return (
    <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${classes}`}>
      {status}
    </span>
  );
}

// ─── Content region ───────────────────────────────────────────────────────────

interface ContentRegionProps {
  nodeId: number;
  contentType?: string;
}

function ContentRegion({ nodeId, contentType }: ContentRegionProps) {
  const { data: content, isFetching, error } = useNodeContent(nodeId);

  useEffect(() => {
    if (error instanceof DivoidApiError) {
      toast.error(`Content: ${error.code}: ${error.text}`);
    }
  }, [error]);

  if (isFetching) {
    return (
      <div className="space-y-2 animate-pulse" aria-label="Loading content">
        {[...Array(6)].map((_, i) => (
          <div key={i} className="h-4 rounded bg-muted" style={{ width: `${70 + (i % 3) * 10}%` }} />
        ))}
      </div>
    );
  }

  if (error) {
    return (
      <p className="text-sm text-muted-foreground italic" role="alert">
        Content unavailable.
      </p>
    );
  }

  if (!content) {
    return <p className="text-sm text-muted-foreground italic">No content.</p>;
  }

  const isMarkdown =
    !contentType ||
    contentType.includes('markdown') ||
    contentType.includes('text/plain');

  if (isMarkdown) {
    return (
      <div
        className="prose prose-sm dark:prose-invert max-w-none"
        aria-label="Node content"
      >
        <ReactMarkdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeSanitize]}>
          {content}
        </ReactMarkdown>
      </div>
    );
  }

  // Non-markdown text — render as pre-formatted
  return (
    <pre className="text-sm font-mono whitespace-pre-wrap break-words bg-muted/40 rounded-md p-4 overflow-x-auto">
      {content}
    </pre>
  );
}

// ─── Neighbours region ────────────────────────────────────────────────────────

function NeighboursRegion({ nodeId }: { nodeId: number }) {
  const { data, isFetching, error } = useNodeListLinkedTo(nodeId, { count: 100 });

  useEffect(() => {
    if (error instanceof DivoidApiError) {
      toast.error(`Neighbours: ${error.code}: ${error.text}`);
    }
  }, [error]);

  return (
    <section aria-labelledby="neighbours-heading">
      <h2 id="neighbours-heading" className="text-sm font-semibold mb-3">
        Linked nodes
        {data && data.total >= 0 && (
          <span className="ml-2 text-muted-foreground font-normal">({data.total})</span>
        )}
      </h2>
      <NodeResultTable nodes={data?.result ?? []} loading={isFetching} />
    </section>
  );
}

// ─── Page ─────────────────────────────────────────────────────────────────────

export function NodeDetailPage() {
  const { id: idParam } = useParams<{ id: string }>();
  const nodeId = idParam ? parseInt(idParam, 10) : 0;

  const { data: node, isFetching, error } = useNode(nodeId);

  useEffect(() => {
    if (error instanceof DivoidApiError && error.status !== 404) {
      toast.error(`${error.code}: ${error.text}`);
    }
  }, [error]);

  if (!idParam || isNaN(nodeId) || nodeId <= 0) {
    return (
      <div className="mx-auto max-w-5xl px-4 py-6">
        <p className="text-sm text-destructive" role="alert">
          Invalid node ID.
        </p>
      </div>
    );
  }

  if (isFetching) {
    return (
      <div className="mx-auto max-w-5xl px-4 py-6 space-y-4">
        <div className="h-7 w-48 rounded bg-muted animate-pulse" />
        <div className="space-y-2">
          {[...Array(4)].map((_, i) => (
            <div key={i} className="h-5 rounded bg-muted animate-pulse" style={{ width: `${40 + i * 15}%` }} />
          ))}
        </div>
      </div>
    );
  }

  if (error instanceof DivoidApiError && error.status === 404) {
    return (
      <div className="mx-auto max-w-5xl px-4 py-6">
        <p className="text-sm text-muted-foreground" role="alert">
          Node {nodeId} not found.
        </p>
      </div>
    );
  }

  if (error && !node) {
    return (
      <div className="mx-auto max-w-5xl px-4 py-6">
        <p className="text-sm text-destructive" role="alert">
          Failed to load node. Please try again.
        </p>
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-5xl px-4 py-6 flex flex-col gap-6">
      {/* Back link */}
      <Link
        to={ROUTES.SEARCH}
        className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground transition-colors"
      >
        <ChevronLeft size={15} aria-hidden="true" />
        Back to search
      </Link>

      {/* Metadata region */}
      <section aria-labelledby="metadata-heading">
        <h1 id="metadata-heading" className="text-xl font-semibold mb-4">
          {node?.name ?? `Node ${nodeId}`}
        </h1>
        <div className="rounded-md border border-border px-4 py-1">
          <MetadataRow label="ID" value={<span className="tabular-nums">{nodeId}</span>} />
          <MetadataRow
            label="Type"
            value={
              node?.type ? (
                <span className="rounded bg-muted px-1.5 py-0.5 text-xs font-mono">
                  {node.type}
                </span>
              ) : (
                '—'
              )
            }
          />
          <MetadataRow label="Status" value={<StatusBadge status={node?.status ?? null} />} />
          <MetadataRow
            label="Content type"
            value={
              <span className="text-muted-foreground text-xs font-mono">
                {node?.contentType ?? '—'}
              </span>
            }
          />
        </div>
      </section>

      {/* Content region — only shown when a contentType is declared */}
      {node?.contentType && (
        <section aria-labelledby="content-heading">
          <h2 id="content-heading" className="text-sm font-semibold mb-3">
            Content
          </h2>
          <ContentRegion nodeId={nodeId} contentType={node.contentType} />
        </section>
      )}

      {/* Neighbours region */}
      <NeighboursRegion nodeId={nodeId} />
    </div>
  );
}
