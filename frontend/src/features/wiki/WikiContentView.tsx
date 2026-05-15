/**
 * WikiContentView — gated content renderer for wiki articles.
 *
 * W1 is read-only. Content is fetched only when the node has a text-shaped
 * contentType (via the `enabled` gate on useNodeContent). Empty-content nodes
 * show a friendly placeholder instead of triggering a backend error.
 *
 * Rendering:
 *  - text/* / application/json → markdown via ReactMarkdown + rehype-sanitize
 *  - binary contentType present → placeholder (download in W2)
 *  - no contentType → "(no content yet)" placeholder
 *
 * Out of scope: "Add markdown" / "Upload" buttons, inline editing (W2).
 *
 * Task: DiVoid node #413
 * Subsumes task #294 (empty-content gating) for the wiki surface.
 */

import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeSanitize from 'rehype-sanitize';
import { useNodeContent } from '@/features/nodes/useNodeContent';
import { isTextShaped } from '@/features/nodes/MarkdownEditorSurface';
import type { NodeDetails } from '@/types/divoid';

interface WikiContentViewProps {
  node: NodeDetails;
}

export function WikiContentView({ node }: WikiContentViewProps) {
  const textShaped = isTextShaped(node.contentType);

  const { data: content, isFetching } = useNodeContent(node.id, {
    enabled: textShaped,
  });

  // Node has no contentType at all.
  if (!node.contentType) {
    return (
      <p className="text-sm text-muted-foreground italic" data-testid="wiki-no-content">
        (no content yet)
      </p>
    );
  }

  // Binary content — full render deferred to W2.
  if (!textShaped) {
    return (
      <p className="text-sm text-muted-foreground italic" data-testid="wiki-binary-content">
        Binary content ({node.contentType}) — download in W2
      </p>
    );
  }

  // Text-shaped: loading skeleton.
  if (isFetching && !content) {
    return (
      <div className="space-y-2 animate-pulse" aria-label="Loading content">
        {[...Array(6)].map((_, i) => (
          <div key={i} className="h-4 rounded bg-muted" style={{ width: `${70 + (i % 3) * 10}%` }} />
        ))}
      </div>
    );
  }

  // Text-shaped: empty body.
  if (!content) {
    return (
      <p className="text-sm text-muted-foreground italic" data-testid="wiki-no-content">
        (no content yet)
      </p>
    );
  }

  // Text-shaped: render markdown.
  return (
    <div
      className="prose prose-sm dark:prose-invert max-w-none"
      aria-label="Node content"
      data-testid="wiki-content"
    >
      <ReactMarkdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeSanitize]}>
        {content}
      </ReactMarkdown>
    </div>
  );
}
