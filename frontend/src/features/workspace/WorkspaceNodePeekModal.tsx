/**
 * WorkspaceNodePeekModal — in-canvas modal overlay for node detail preview.
 *
 * Wraps NodeDetailView in a Radix Dialog. Controlled by peekId from the URL
 * (?peek=<id>) via WorkspacePage. When peekId is null the dialog is closed and
 * NodeDetailView is not mounted — no queries fire.
 *
 * Accessibility:
 *  - Focus trap and return-focus are Radix defaults.
 *  - ESC is intercepted by Radix and does not reach xyflow's deleteKeyCode.
 *
 * Design: docs/architecture/workspace-modal-preview.md §5.3 / §8.2
 * Task: DiVoid node #1253
 */

import * as Dialog from '@radix-ui/react-dialog';
import { X } from 'lucide-react';
import { NodeDetailView } from '@/features/nodes/NodeDetailView';
import { useNode } from '@/features/nodes/useNode';

interface WorkspaceNodePeekModalProps {
  /** Which node to peek. null means the dialog is closed. */
  peekId: number | null;
  /** Called when Radix closes the dialog (overlay click, ESC, X button) or when
   *  an internal action implies close (Delete success). */
  onClose: () => void;
  /** Called when the user clicks a neighbour row — swaps the current peek. */
  onPeekChange: (id: number) => void;
}

/**
 * In-canvas modal that presents NodeDetailView for the currently peeked node.
 *
 * The dialog content is only mounted when peekId is non-null. NodeDetailView's
 * three queries (useNode, useNodeContent, useNodeListLinkedTo) mount on open and
 * unmount on close, with TanStack Query's GC keeping data warm for staleTime.
 *
 * The modal lives at WorkspacePage level (sibling of WorkspaceCanvas), so peek
 * state changes do not cascade into the canvas — see design §9.
 */
export function WorkspaceNodePeekModal({
  peekId,
  onClose,
  onPeekChange,
}: WorkspaceNodePeekModalProps) {
  const { data: node } = useNode(peekId ?? 0);
  const title = node?.name || (peekId !== null ? `Node ${peekId}` : 'Node detail');

  return (
    <Dialog.Root open={peekId !== null} onOpenChange={(open) => { if (!open) onClose(); }}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-black/40 z-40 data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0" />
        <Dialog.Content
          className="fixed z-50 left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 flex flex-col w-[calc(100vw-1rem)] max-w-[96rem] h-[calc(100vh-1rem)] supports-[height:100dvh]:h-[calc(100dvh-1rem)] sm:w-[calc(100vw-2rem)] sm:h-[calc(100vh-2rem)] sm:supports-[height:100dvh]:h-[calc(100dvh-2rem)] overflow-hidden bg-background border border-border rounded-lg shadow-lg focus:outline-none data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 data-[state=closed]:zoom-out-95 data-[state=open]:zoom-in-95"
          aria-describedby="peek-modal-description"
          data-testid="workspace-peek-modal"
        >
          <div className="flex shrink-0 items-center justify-between gap-3 px-4 py-4 sm:px-6 border-b border-border bg-background">
            <Dialog.Title className="min-w-0 truncate text-base font-semibold">
              {title}
            </Dialog.Title>
            <Dialog.Close asChild>
              <button
                aria-label="Close preview"
                className="shrink-0 rounded-md p-1 text-muted-foreground hover:text-foreground hover:bg-muted transition-colors"
              >
                <X size={16} aria-hidden="true" />
              </button>
            </Dialog.Close>
          </div>

          <Dialog.Description id="peek-modal-description" className="sr-only">
            {peekId !== null ? `Detail view for ${title}` : ''}
          </Dialog.Description>

          {/* Body — NodeDetailView mounts only when peekId is set */}
          <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain px-4 py-4 sm:px-6 sm:py-6">
            {peekId !== null && (
              <NodeDetailView
                nodeId={peekId}
                onClose={onClose}
                onNeighbourClick={onPeekChange}
              />
            )}
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
