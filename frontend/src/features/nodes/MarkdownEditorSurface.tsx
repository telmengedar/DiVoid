/**
 * MarkdownEditorSurface — inline markdown text editor for node content.
 *
 * Surfaces on the node detail page alongside ContentUploadZone (drag-drop).
 * The two are complementary: this handles text/markdown; the zone handles binary.
 *
 * Behaviour:
 *  - When the node already has text-shaped content (text/* or application/json),
 *    the editor is pre-loaded with the current content via useNodeContent.
 *  - Two tabs: "Write" (plain <textarea>) and "Preview" (same ReactMarkdown
 *    renderer used on the read path — no duplicate library).
 *  - Save commits via useUploadContent with Content-Type: text/markdown; charset=utf-8.
 *    The mutation's onSuccess invalidates nodeContentQueryKey so the read path
 *    re-fetches automatically. Additional keys can be invalidated via
 *    extraInvalidationKeys (e.g. broad ['nodes'] prefix for wiki — Section 8.2).
 *  - Save is triggered only by an explicit button click — never in a useEffect,
 *    never with draft text in any dependency array (render-loop guard).
 *  - onSaved / onCancel callbacks let the caller control post-save / cancel
 *    side-effects (mode transitions, navigation). Both default to no-ops.
 *
 * Out of scope: image-paste, slash commands, AI assist, collaborative editing.
 *
 * Design: docs/architecture/frontend-bootstrap.md §5.6
 * Task: DiVoid node #276
 * Section 14.3 fix: DiVoid node #421 — callback props eliminate the need for
 * a second WikiMarkdownEditor sub-component.
 */

import { useState, useCallback } from 'react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeSanitize from 'rehype-sanitize';
import { useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Save, Eye, Pencil, X } from 'lucide-react';
import { useUploadContent } from './mutations';
import { cn } from '@/lib/cn';

interface MarkdownEditorSurfaceProps {
  nodeId: number;
  /** Pre-populated text. Pass the current content string when it exists. */
  initialContent?: string;
  /**
   * Called after the save mutation succeeds. The caller controls cache
   * invalidation for its own scope here (e.g. broad ['nodes'] prefix for
   * wiki — Section 8.2; narrow per-node for NodeDetailPage). If omitted,
   * no caller-side side-effect runs beyond the surface's built-in narrow
   * invalidation inside useUploadContent.
   */
  onSaved?: () => void;
  /**
   * When provided, renders a Cancel button that invokes this callback.
   * If omitted, no Cancel button is shown.
   */
  onCancel?: () => void;
  /**
   * Additional TanStack Query key arrays to invalidate broadly after a
   * successful save. The built-in narrow nodeContent/nodeDetail invalidation
   * inside useUploadContent always fires regardless.
   * Example: pass [['nodes']] for the broad Section 8.2 wiki invalidation.
   */
  extraInvalidationKeys?: readonly unknown[][];
}

type EditorTab = 'write' | 'preview';

/**
 * Returns true when the Content-Type is text-shaped and the editor should
 * attempt to pre-load existing content.
 */
export function isTextShaped(contentType: string | null | undefined): boolean {
  if (!contentType) return false;
  const ct = contentType.toLowerCase();
  return ct.startsWith('text/') || ct.startsWith('application/json');
}

export function MarkdownEditorSurface({
  nodeId,
  initialContent = '',
  onSaved,
  onCancel,
  extraInvalidationKeys,
}: MarkdownEditorSurfaceProps) {
  const [tab, setTab] = useState<EditorTab>('write');
  const [draft, setDraft] = useState(initialContent);
  const mutation = useUploadContent(nodeId);
  const queryClient = useQueryClient();

  const handleSave = useCallback(() => {
    const bytes = new TextEncoder().encode(draft);
    mutation.mutate(
      { body: bytes, contentType: 'text/markdown; charset=utf-8' },
      {
        onSuccess: () => {
          if (extraInvalidationKeys) {
            for (const key of extraInvalidationKeys) {
              queryClient.invalidateQueries({ queryKey: key as unknown[] });
            }
          }
          toast.success('Content saved.');
          onSaved?.();
        },
      },
    );
  }, [draft, mutation, queryClient, extraInvalidationKeys, onSaved]);

  return (
    <div className="flex flex-col gap-0 rounded-lg border border-border overflow-hidden">
      {/* Tab bar */}
      <div className="flex items-center border-b border-border bg-muted/40 px-1 pt-1 gap-0.5">
        <button
          type="button"
          role="tab"
          aria-selected={tab === 'write'}
          aria-controls="editor-write-panel"
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
          aria-controls="editor-preview-panel"
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
        id="editor-write-panel"
        role="tabpanel"
        aria-label="Write markdown"
        hidden={tab !== 'write'}
      >
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
        id="editor-preview-panel"
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
        {onCancel && (
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
        )}
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
