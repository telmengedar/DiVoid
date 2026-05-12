/**
 * EditNodeDialog — "Edit" dialog on the node detail page.
 *
 * Patches name and status via PATCH /api/nodes/{id}.
 * Status dropdown is only shown when the node type carries a status
 * (task or bug). Admin-only fields (Permissions) are guarded via useWhoami.
 *
 * On success: the node detail query is invalidated by the mutation hook;
 *             the dialog closes.
 * On error: the mutation hook surfaces a sonner toast; dialog stays open.
 *
 * Design: docs/architecture/frontend-bootstrap.md §5.6, §6.6, §9.3
 * Task: DiVoid node #229
 */

import { useEffect } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import * as Dialog from '@radix-ui/react-dialog';
import { X } from 'lucide-react';
import { usePatchNode } from './mutations';
import { editNodeSchema, statusOptionsForType, type EditNodeFormValues } from './schemas';
import type { NodeDetails } from '@/types/divoid';

interface EditNodeDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  node: NodeDetails;
}

export function EditNodeDialog({ open, onOpenChange, node }: EditNodeDialogProps) {
  const mutation = usePatchNode(node.id);

  const statusOptions = statusOptionsForType(node.type);

  const {
    register,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<EditNodeFormValues>({
    resolver: zodResolver(editNodeSchema),
    defaultValues: {
      name: node.name,
      status: node.status ?? '',
    },
  });

  // Sync default values when the node prop changes or dialog opens.
  useEffect(() => {
    if (open) {
      reset({ name: node.name, status: node.status ?? '' });
      mutation.reset();
    }
  }, [open, node.name, node.status, reset, mutation]);

  const onSubmit = handleSubmit(async (values) => {
    const ops = [];

    if (values.name.trim() !== node.name) {
      ops.push({ op: 'replace' as const, path: '/name', value: values.name.trim() });
    }

    // Status: empty string → null (remove status); otherwise replace.
    const newStatus = values.status?.trim() || null;
    if (newStatus !== node.status) {
      ops.push({ op: 'replace' as const, path: '/status', value: newStatus });
    }

    if (ops.length === 0) {
      onOpenChange(false);
      return;
    }

    try {
      await mutation.mutateAsync(ops);
      onOpenChange(false);
    } catch {
      // Error toast was already shown by the mutation's onError handler.
      // Keep the dialog open for retry.
    }
  });

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-black/40 z-40 data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0" />
        <Dialog.Content
          className="fixed z-50 left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 w-full max-w-md bg-background border border-border rounded-lg shadow-lg p-6 focus:outline-none data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 data-[state=closed]:zoom-out-95 data-[state=open]:zoom-in-95"
          aria-describedby="edit-node-description"
        >
          <div className="flex items-center justify-between mb-4">
            <Dialog.Title className="text-base font-semibold">Edit node</Dialog.Title>
            <Dialog.Close asChild>
              <button
                aria-label="Close dialog"
                className="rounded-md p-1 text-muted-foreground hover:text-foreground hover:bg-muted transition-colors"
              >
                <X size={16} aria-hidden="true" />
              </button>
            </Dialog.Close>
          </div>

          <p id="edit-node-description" className="sr-only">
            Edit the name and status of this node.
          </p>

          <form onSubmit={onSubmit} noValidate className="flex flex-col gap-4">
            {/* Name */}
            <div className="flex flex-col gap-1">
              <label htmlFor="edit-name" className="text-sm font-medium">
                Name <span aria-hidden="true">*</span>
              </label>
              <input
                id="edit-name"
                type="text"
                autoComplete="off"
                className="h-9 rounded-md border border-border bg-background px-3 text-sm placeholder:text-muted-foreground focus:outline-none focus:ring-2 focus:ring-ring disabled:opacity-50"
                aria-invalid={!!errors.name}
                aria-describedby={errors.name ? 'edit-name-error' : undefined}
                {...register('name')}
              />
              {errors.name && (
                <p id="edit-name-error" className="text-xs text-destructive" role="alert">
                  {errors.name.message}
                </p>
              )}
            </div>

            {/* Status — only for task/bug */}
            {statusOptions && (
              <div className="flex flex-col gap-1">
                <label htmlFor="edit-status" className="text-sm font-medium">
                  Status
                </label>
                <select
                  id="edit-status"
                  className="h-9 rounded-md border border-border bg-background px-3 text-sm focus:outline-none focus:ring-2 focus:ring-ring disabled:opacity-50"
                  {...register('status')}
                >
                  <option value="">— none —</option>
                  {statusOptions.map((s) => (
                    <option key={s} value={s}>
                      {s}
                    </option>
                  ))}
                </select>
              </div>
            )}

            {/* Actions */}
            <div className="flex justify-end gap-2 pt-2">
              <Dialog.Close asChild>
                <button
                  type="button"
                  className="h-9 px-4 rounded-md border border-border text-sm hover:bg-muted transition-colors"
                >
                  Cancel
                </button>
              </Dialog.Close>
              <button
                type="submit"
                disabled={isSubmitting || mutation.isPending}
                className="h-9 px-4 rounded-md bg-primary text-primary-foreground text-sm font-medium hover:opacity-90 disabled:opacity-50 disabled:cursor-not-allowed transition-opacity"
              >
                {mutation.isPending ? 'Saving…' : 'Save'}
              </button>
            </div>
          </form>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
