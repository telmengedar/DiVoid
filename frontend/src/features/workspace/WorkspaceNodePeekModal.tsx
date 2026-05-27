/**
 * WorkspaceNodePeekModal — in-canvas modal overlay for node detail preview.
 *
 * Wraps NodeDetailView in a Radix Dialog. Controlled by peekId from the URL
 * (?peek=<id>) via WorkspacePage. When peekId is null the dialog is closed and
 * NodeDetailView is not mounted — no queries fire.
 *
 * Accessibility:
 *  - Dialog.Title shows the node id (changes to node name once loaded — handled
 *    by NodeDetailView's own heading; the dialog title is sr-only supplemental).
 *  - Dialog.Description is sr-only: "Detail view for node N".
 *  - Focus trap and return-focus are Radix defaults.
 *  - ESC is intercepted by Radix and does not reach xyflow's deleteKeyCode.
 *
 * Design: docs/architecture/workspace-modal-preview.md §5.3 / §8.2
 * Task: DiVoid node #1253
 */

import * as Dialog from '@radix-ui/react-dialog';
import { X } from 'lucide-react';
import { NodeDetailView } from '@/features/nodes/NodeDetailView';

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
  return (
    <Dialog.Root open={peekId !== null} onOpenChange={(open) => { if (!open) onClose(); }}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-black/40 z-40 data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0" />
        <Dialog.Content
          className="fixed z-50 left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 w-full max-w-3xl max-h-[85vh] overflow-y-auto bg-background border border-border rounded-lg shadow-lg focus:outline-none data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 data-[state=closed]:zoom-out-95 data-[state=open]:zoom-in-95"
          aria-describedby="peek-modal-description"
          data-testid="workspace-peek-modal"
        >
          {/* Modal header — close button + sr-only title */}
          <div className="sticky top-0 z-10 flex items-center justify-between px-6 py-4 border-b border-border bg-background">
            <Dialog.Title className="text-base font-semibold">
              {peekId !== null ? `Node ${peekId}` : 'Node detail'}
            </Dialog.Title>
            <Dialog.Close asChild>
              <button
                aria-label="Close preview"
                className="rounded-md p-1 text-muted-foreground hover:text-foreground hover:bg-muted transition-colors"
              >
                <X size={16} aria-hidden="true" />
              </button>
            </Dialog.Close>
          </div>

          <Dialog.Description id="peek-modal-description" className="sr-only">
            {peekId !== null ? `Detail view for node ${peekId}` : ''}
          </Dialog.Description>

          {/* Body — NodeDetailView mounts only when peekId is set */}
          <div className="px-6 py-6">
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
