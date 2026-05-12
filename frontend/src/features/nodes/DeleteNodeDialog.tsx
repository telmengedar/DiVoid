/**
 * DeleteNodeDialog — delete confirmation dialog for the node detail page.
 *
 * Presents a confirmation prompt before calling DELETE /api/nodes/{id}.
 * On confirm: mutation fires; on success the onDeleted callback is called
 *             (caller navigates to /nodes).
 * On error: the mutation hook surfaces a sonner toast; dialog stays open.
 *
 * Design: docs/architecture/frontend-bootstrap.md §5.6, §9.3
 * Task: DiVoid node #229
 */

import * as Dialog from '@radix-ui/react-dialog';
import { X, Trash2 } from 'lucide-react';
import { useDeleteNode } from './mutations';
import type { NodeDetails } from '@/types/divoid';

interface DeleteNodeDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  node: NodeDetails;
  onDeleted: () => void;
}

export function DeleteNodeDialog({
  open,
  onOpenChange,
  node,
  onDeleted,
}: DeleteNodeDialogProps) {
  const mutation = useDeleteNode(node.id);

  const handleConfirm = async () => {
    // mutateAsync rejects on error; catch means error was handled (toast shown by hook).
    // Only call onDeleted when the mutation actually succeeded.
    try {
      await mutation.mutateAsync();
      onOpenChange(false);
      onDeleted();
    } catch {
      // Error toast was already shown by the mutation's onError handler.
      // Keep the dialog open for retry.
    }
  };

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-black/40 z-40 data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0" />
        <Dialog.Content
          className="fixed z-50 left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 w-full max-w-sm bg-background border border-border rounded-lg shadow-lg p-6 focus:outline-none data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 data-[state=closed]:zoom-out-95 data-[state=open]:zoom-in-95"
          aria-describedby="delete-node-description"
        >
          <div className="flex items-start justify-between mb-4">
            <Dialog.Title className="text-base font-semibold text-destructive">
              Delete node
            </Dialog.Title>
            <Dialog.Close asChild>
              <button
                aria-label="Close dialog"
                className="rounded-md p-1 text-muted-foreground hover:text-foreground hover:bg-muted transition-colors"
              >
                <X size={16} aria-hidden="true" />
              </button>
            </Dialog.Close>
          </div>

          <p id="delete-node-description" className="text-sm text-muted-foreground mb-6">
            Delete{' '}
            <span className="font-medium text-foreground">{node.name}</span> (id {node.id})? This
            also removes all its links. This action cannot be undone.
          </p>

          <div className="flex justify-end gap-2">
            <Dialog.Close asChild>
              <button
                type="button"
                className="h-9 px-4 rounded-md border border-border text-sm hover:bg-muted transition-colors"
              >
                Cancel
              </button>
            </Dialog.Close>
            <button
              type="button"
              onClick={handleConfirm}
              disabled={mutation.isPending}
              className="inline-flex items-center gap-1.5 h-9 px-4 rounded-md bg-destructive text-destructive-foreground text-sm font-medium hover:opacity-90 disabled:opacity-50 disabled:cursor-not-allowed transition-opacity"
            >
              <Trash2 size={14} aria-hidden="true" />
              {mutation.isPending ? 'Deleting…' : 'Delete'}
            </button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
