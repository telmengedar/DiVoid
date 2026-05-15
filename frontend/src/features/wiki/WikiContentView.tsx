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
 *  - MarkdownEditorSurface textarea carries both light + dark Tailwind classes
 *    (Section 14.2), closing the textarea-theming half of bug #281.
 *  - Save → POST /content via useUploadContent with broad ['nodes'] prefix
 *    invalidation per Section 8.2. The narrow nodeContent/nodeDetail keys
 *    are also invalidated so the read path re-fetches.
 *  - All side effects are in useEffect or mutation callbacks — never the
 *    render body (Section 10.2).
 *
 * Task: DiVoid node #421
 * Closes: bug #294 (empty-state editor/upload half), bug #281 (textarea half)
 */

import { useState, useCallback } from 'react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeSanitize from 'rehype-sanitize';
import { useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Pencil, X, FileText, UploadCloud, Save, Eye } from 'lucide-react';
import { useNodeContent } from '@/features/nodes/useNodeContent';
import { useUploadContent } from '@/features/nodes/mutations';
import { isTextShaped } from '@/features/nodes/MarkdownEditorSurface';
import { ContentUploadZone } from '@/features/nodes/ContentUploadZone';
import { cn } from '@/lib/cn';
import type { NodeDetails } from '@/types/divoid';

interface WikiContentViewProps {
  node: NodeDetails;
}

type ContentMode = 'read' | 'edit' | 'compose-markdown' | 'compose-upload';

// ─── WikiMarkdownEditor ───────────────────────────────────────────────────────

/**
 * Inline markdown editor for wiki articles.
 *
 * Used both for editing existing text-shaped content and for composing new
 * markdown on an empty node (compose mode). Mirrors MarkdownEditorSurface
 * but wires broad ['nodes'] invalidation (Section 8.2) rather than the
 * narrow per-node key that MarkdownEditorSurface uses for NodeDetailPage.
 *
 * Theme-awareness: the textarea carries both light-mode and dark-mode Tailwind
 * classes, satisfying Section 14.2 and closing the textarea half of bug #281.
 */
interface WikiMarkdownEditorProps {
  nodeId: number;
  initialContent?: string;
  onCancel: () => void;
  onSaved: () => void;
}

function WikiMarkdownEditor({ nodeId, initialContent = '', onCancel, onSaved }: WikiMarkdownEditorProps) {
  const [tab, setTab] = useState<'write' | 'preview'>('write');
  const [draft, setDraft] = useState(initialContent);
  const mutation = useUploadContent(nodeId);
  const queryClient = useQueryClient();

  const handleSave = useCallback(() => {
    const bytes = new TextEncoder().encode(draft);
    mutation.mutate(
      { body: bytes, contentType: 'text/markdown; charset=utf-8' },
      {
        onSuccess: () => {
          // Broad invalidation per Section 8.2 — covers all node-related views
          // that may cache this node's content, name, or contentType.
          queryClient.invalidateQueries({ queryKey: ['nodes'] });
          toast.success('Content saved.');
          onSaved();
        },
      },
    );
  }, [draft, mutation, queryClient, onSaved]);

  return (
    <div className="flex flex-col gap-0 rounded-lg border border-border overflow-hidden">
      {/* Tab bar */}
      <div className="flex items-center border-b border-border bg-muted/40 px-1 pt-1 gap-0.5">
        <button
          type="button"
          role="tab"
          aria-selected={tab === 'write'}
          aria-controls="wiki-editor-write-panel"
          onClick={() => setTab('write')}
          className={cn(
            'inline-flex items-center gap-1.5 rounded-t px-3 py-1.5 text-xs font-medium transition-colors',
            tab === 'write'
              ? 'bg-background text-foreground shadow-sm'
              : 'text-muted-foreground hover:text-foreground',
          )}
        >
          <Pencil size={12} aria-hidden="true" />
          Write
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={tab === 'preview'}
          aria-controls="wiki-editor-preview-panel"
          onClick={() => setTab('preview')}
          className={cn(
            'inline-flex items-center gap-1.5 rounded-t px-3 py-1.5 text-xs font-medium transition-colors',
            tab === 'preview'
              ? 'bg-background text-foreground shadow-sm'
              : 'text-muted-foreground hover:text-foreground',
          )}
        >
          <Eye size={12} aria-hidden="true" />
          Preview
        </button>
      </div>

      {/* Write panel */}
      <div
        id="wiki-editor-write-panel"
        role="tabpanel"
        aria-label="Write markdown"
        hidden={tab !== 'write'}
      >
        {/* Section 14.2: both light-mode AND dark-mode classes on the textarea.
            Closes the textarea-theming half of bug #281. */}
        <textarea
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          aria-label="Markdown content editor"
          aria-multiline="true"
          spellCheck
          rows={12}
          data-testid="wiki-editor-textarea"
          className={cn(
            'w-full resize-y px-4 py-3 text-sm font-mono',
            'bg-background dark:bg-background/80',
            'text-foreground dark:text-foreground',
            'focus:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-inset',
            'placeholder:text-muted-foreground dark:placeholder:text-muted-foreground',
          )}
          placeholder="Write markdown here…"
          disabled={mutation.isPending}
        />
      </div>

      {/* Preview panel */}
      <div
        id="wiki-editor-preview-panel"
        role="tabpanel"
        aria-label="Markdown preview"
        hidden={tab !== 'preview'}
      >
        {draft.trim() ? (
          <div className="prose prose-sm dark:prose-invert max-w-none px-4 py-3">
            <ReactMarkdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeSanitize]}>
              {draft}
            </ReactMarkdown>
          </div>
        ) : (
          <p className="px-4 py-3 text-sm text-muted-foreground italic">Nothing to preview.</p>
        )}
      </div>

      {/* Action bar */}
      <div className="flex items-center justify-end gap-2 border-t border-border bg-muted/20 px-3 py-2">
        <button
          type="button"
          onClick={onCancel}
          disabled={mutation.isPending}
          aria-label="Cancel editing"
          className="inline-flex items-center gap-1.5 h-7 px-3 rounded-md text-xs font-medium border border-border hover:bg-muted transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
        >
          <X size={12} aria-hidden="true" />
          Cancel
        </button>
        <button
          type="button"
          onClick={handleSave}
          disabled={mutation.isPending}
          aria-label="Save markdown content"
          data-testid="wiki-editor-save"
          className={cn(
            'inline-flex items-center gap-1.5 h-7 px-3 rounded-md text-xs font-medium transition-colors',
            'bg-primary text-primary-foreground hover:bg-primary/90',
            'disabled:opacity-50 disabled:cursor-not-allowed',
          )}
        >
          <Save size={12} aria-hidden="true" />
          {mutation.isPending ? 'Saving…' : 'Save'}
        </button>
      </div>
    </div>
  );
}

// ─── WikiContentView ──────────────────────────────────────────────────────────

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
        <WikiMarkdownEditor
          nodeId={node.id}
          initialContent=""
          onCancel={() => setMode('read')}
          onSaved={() => setMode('read')}
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
      <WikiMarkdownEditor
        nodeId={node.id}
        initialContent={content ?? ''}
        onCancel={() => setMode('read')}
        onSaved={() => setMode('read')}
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
