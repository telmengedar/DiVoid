/**
 * WikiContentView — gated content renderer and editor for wiki articles.
 *
 * W2 adds write surfaces on top of W1's read-only spine:
 *  - Edit toggle for text-shaped content (mirrors NodeDetailPage.tsx:122-159).
 *  - Empty-state "Add markdown" / "Upload file" buttons when !contentType.
 *
 * Rendering:
 *  - text/* / application/json → markdown via ReactMarkdown + rehype-sanitize.
 *  - binary contentType present → placeholder (download deferred).
 *  - no contentType → empty-state buttons (W2) while node is loaded;
 *    skeleton while node is loading.
 *
 * Write:
 *  - MarkdownEditorSurface carries both light + dark Tailwind classes
 *    (Section 14.2), closing the textarea-theming half of bug #281.
 *  - Save → POST /content via useUploadContent with broad ['nodes'] prefix
 *    invalidation per Section 8.2 (via extraInvalidationKeys). The narrow
 *    nodeContent/nodeDetail keys are also invalidated by useUploadContent so
 *    the read path re-fetches.
 *  - All side effects are in mutation callbacks — never the render body
 *    (Section 10.2).
 *  - Section 14.3: uses shared MarkdownEditorSurface with onSaved/onCancel/
 *    extraInvalidationKeys rather than a local WikiMarkdownEditor copy.
 *
 * Task: DiVoid node #421
 * Closes: bug #294 (empty-state editor/upload half), bug #281 (textarea half)
 */

import { useState } from 'react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeSanitize from 'rehype-sanitize';
import { Pencil, X, FileText, UploadCloud } from 'lucide-react';
import { useNodeContent } from '@/features/nodes/useNodeContent';
import { isTextShaped, MarkdownEditorSurface } from '@/features/nodes/MarkdownEditorSurface';
import { ContentUploadZone } from '@/features/nodes/ContentUploadZone';
import { cn } from '@/lib/cn';
import type { NodeDetails } from '@/types/divoid';

// Broad invalidation key for the wiki write path (Section 8.2).
// Passed as extraInvalidationKeys to MarkdownEditorSurface so the shared
// surface can apply wiki-scope invalidation without hardcoding it internally.
const WIKI_INVALIDATION_KEYS: readonly unknown[][] = [['nodes']];

interface WikiContentViewProps {
  node: NodeDetails;
}

type ContentMode = 'read' | 'edit' | 'compose-markdown' | 'compose-upload';

export function WikiContentView({ node }: WikiContentViewProps) {
  const textShaped = isTextShaped(node.contentType);
  const [mode, setMode] = useState<ContentMode>('read');

  const { data: content, isFetching } = useNodeContent(node.id, {
    enabled: textShaped,
  });

  // ── Empty-state: node loaded but has no contentType ──
  if (!node.contentType) {
    if (mode === 'compose-markdown') {
      return (
        <MarkdownEditorSurface
          nodeId={node.id}
          initialContent=""
          onSaved={() => setMode('read')}
          onCancel={() => setMode('read')}
          extraInvalidationKeys={WIKI_INVALIDATION_KEYS}
        />
      );
    }

    if (mode === 'compose-upload') {
      return (
        <div className="flex flex-col gap-3">
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={() => setMode('read')}
              aria-label="Cancel upload"
              className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground transition-colors"
            >
              <X size={14} aria-hidden="true" />
              Cancel
            </button>
          </div>
          <ContentUploadZone nodeId={node.id} />
        </div>
      );
    }

    // Default empty-state: two action buttons.
    return (
      <div className="flex flex-col gap-3" data-testid="wiki-empty-state">
        <p className="text-sm text-muted-foreground italic">
          This page has no content yet.
        </p>
        <div className="flex items-center gap-2">
          <button
            type="button"
            onClick={() => setMode('compose-markdown')}
            aria-label="Add markdown content"
            data-testid="wiki-add-markdown-btn"
            className={cn(
              'inline-flex items-center gap-1.5 h-8 px-3 rounded-md border border-border text-sm',
              'text-foreground dark:text-foreground',
              'hover:bg-muted dark:hover:bg-muted/60 transition-colors',
            )}
          >
            <FileText size={14} aria-hidden="true" />
            Add markdown
          </button>
          <button
            type="button"
            onClick={() => setMode('compose-upload')}
            aria-label="Upload file content"
            data-testid="wiki-upload-file-btn"
            className={cn(
              'inline-flex items-center gap-1.5 h-8 px-3 rounded-md border border-border text-sm',
              'text-foreground dark:text-foreground',
              'hover:bg-muted dark:hover:bg-muted/60 transition-colors',
            )}
          >
            <UploadCloud size={14} aria-hidden="true" />
            Upload file
          </button>
        </div>
      </div>
    );
  }

  // ── Binary content — full render deferred. ──
  if (!textShaped) {
    return (
      <p className="text-sm text-muted-foreground italic" data-testid="wiki-binary-content">
        Binary content ({node.contentType}) — download in W2
      </p>
    );
  }

  // ── Text-shaped: loading skeleton. ──
  if (isFetching && !content) {
    return (
      <div className="space-y-2 animate-pulse" aria-label="Loading content">
        {[...Array(6)].map((_, i) => (
          <div key={i} className="h-4 rounded bg-muted" style={{ width: `${70 + (i % 3) * 10}%` }} />
        ))}
      </div>
    );
  }

  // ── Text-shaped: edit mode. ──
  if (mode === 'edit') {
    return (
      <MarkdownEditorSurface
        nodeId={node.id}
        initialContent={content ?? ''}
        onSaved={() => setMode('read')}
        onCancel={() => setMode('read')}
        extraInvalidationKeys={WIKI_INVALIDATION_KEYS}
      />
    );
  }

  // ── Text-shaped: read mode with edit toggle. ──
  return (
    <div className="flex flex-col gap-4">
      {/* Edit toggle */}
      <div className="flex items-center gap-2">
        <button
          type="button"
          onClick={() => setMode('edit')}
          aria-label="Edit content"
          data-testid="wiki-edit-btn"
          className="inline-flex items-center gap-1.5 self-start text-sm text-muted-foreground hover:text-foreground transition-colors"
        >
          <Pencil size={15} aria-hidden="true" />
          Edit
        </button>
      </div>

      {/* Read display */}
      {!content ? (
        <p className="text-sm text-muted-foreground italic" data-testid="wiki-no-content">
          (no content yet)
        </p>
      ) : (
        <div
          className="prose prose-sm dark:prose-invert max-w-none"
          aria-label="Node content"
          data-testid="wiki-content"
        >
          <ReactMarkdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeSanitize]}>
            {content}
          </ReactMarkdown>
        </div>
      )}
    </div>
  );
}
