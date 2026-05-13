/**
 * CreateNodeDialog — "New node" dialog for the /nodes browse page.
 *
 * Shows a form with:
 *  - type (free-text input)
 *  - name (required text input)
 *  - status dropdown (only rendered when type is "task" or "bug")
 *
 * On success: calls the onCreated callback with the new node id.
 * On error: the mutation hook surfaces a sonner toast; the dialog stays open
 *           so the user can correct and retry.
 *
 * Design: docs/architecture/frontend-bootstrap.md §5.6, §6.6
 * Task: DiVoid node #229
 */

import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import * as Dialog from '@radix-ui/react-dialog';
import { X } from 'lucide-react';
import { useCreateNode } from './mutations';
import { createNodeSchema, statusOptionsForType, type CreateNodeFormValues } from './schemas';

interface CreateNodeDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onCreated: (id: number) => void;
  /**
   * When provided (workspace canvas use), the new node will be created at
   * this canvas-world position and the position fields will be shown to the user.
   */
  initialPosition?: { x: number; y: number };
}

export function CreateNodeDialog({ open, onOpenChange, onCreated, initialPosition }: CreateNodeDialogProps) {
  const mutation = useCreateNode();

  const {
    register,
    handleSubmit,
    watch,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<CreateNodeFormValues>({
    resolver: zodResolver(createNodeSchema),
    defaultValues: { type: '', name: '', status: undefined },
  });

  const typeValue = watch('type');
  const statusOptions = statusOptionsForType(typeValue);

  // When the dialog closes, reset the form and mutation state.
  // Done in onOpenChange (an event) rather than useEffect to avoid depending on
  // the mutation result object, which TanStack Query recreates every render and
  // would cause an infinite re-render loop.
  const handleOpenChange = (isOpen: boolean) => {
    if (!isOpen) {
      reset();
      mutation.reset();
    }
    onOpenChange(isOpen);
  };

  const onSubmit = handleSubmit(async (values) => {
    const input = {
      type: values.type.trim(),
      name: values.name.trim(),
      ...(values.status && statusOptions ? { status: values.status } : {}),
      ...(initialPosition != null ? { x: initialPosition.x, y: initialPosition.y } : {}),
    };

    try {
      const result = await mutation.mutateAsync(input);
      onCreated(result.id);
      handleOpenChange(false);
    } catch {
      // Error toast was already shown by the mutation's onError handler.
      // Keep the dialog open for retry.
    }
  });

  return (
    <Dialog.Root open={open} onOpenChange={handleOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-black/40 z-40 data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0" />
        <Dialog.Content
          className="fixed z-50 left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 w-full max-w-md bg-background border border-border rounded-lg shadow-lg p-6 focus:outline-none data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 data-[state=closed]:zoom-out-95 data-[state=open]:zoom-in-95"
          aria-describedby="create-node-description"
        >
          <div className="flex items-center justify-between mb-4">
            <Dialog.Title className="text-base font-semibold">New node</Dialog.Title>
            <Dialog.Close asChild>
              <button
                aria-label="Close dialog"
                className="rounded-md p-1 text-muted-foreground hover:text-foreground hover:bg-muted transition-colors"
              >
                <X size={16} aria-hidden="true" />
              </button>
            </Dialog.Close>
          </div>

          <p id="create-node-description" className="sr-only">
            Create a new node in the DiVoid graph.
            {initialPosition != null &&
              ` The node will be placed at canvas position X: ${initialPosition.x.toFixed(0)}, Y: ${initialPosition.y.toFixed(0)}.`}
          </p>

          {initialPosition != null && (
            <p className="text-xs text-muted-foreground mb-3" aria-hidden="true">
              Canvas position: ({initialPosition.x.toFixed(0)}, {initialPosition.y.toFixed(0)})
            </p>
          )}

          <form onSubmit={onSubmit} noValidate className="flex flex-col gap-4">
            {/* Type */}
            <div className="flex flex-col gap-1">
              <label htmlFor="create-type" className="text-sm font-medium">
                Type <span aria-hidden="true">*</span>
              </label>
              <input
                id="create-type"
                type="text"
                placeholder="task, documentation, project…"
                autoComplete="off"
                className="h-9 rounded-md border border-border bg-background px-3 text-sm placeholder:text-muted-foreground focus:outline-none focus:ring-2 focus:ring-ring disabled:opacity-50"
                aria-invalid={!!errors.type}
                aria-describedby={errors.type ? 'create-type-error' : undefined}
                {...register('type')}
              />
              {errors.type && (
                <p id="create-type-error" className="text-xs text-destructive" role="alert">
                  {errors.type.message}
                </p>
              )}
            </div>

            {/* Name */}
            <div className="flex flex-col gap-1">
              <label htmlFor="create-name" className="text-sm font-medium">
                Name <span aria-hidden="true">*</span>
              </label>
              <input
                id="create-name"
                type="text"
                placeholder="Node name"
                autoComplete="off"
                className="h-9 rounded-md border border-border bg-background px-3 text-sm placeholder:text-muted-foreground focus:outline-none focus:ring-2 focus:ring-ring disabled:opacity-50"
                aria-invalid={!!errors.name}
                aria-describedby={errors.name ? 'create-name-error' : undefined}
                {...register('name')}
              />
              {errors.name && (
                <p id="create-name-error" className="text-xs text-destructive" role="alert">
                  {errors.name.message}
                </p>
              )}
            </div>

            {/* Status — only for task/bug */}
            {statusOptions && (
              <div className="flex flex-col gap-1">
                <label htmlFor="create-status" className="text-sm font-medium">
                  Status
                </label>
                <select
                  id="create-status"
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
                {mutation.isPending ? 'Creating…' : 'Create'}
              </button>
            </div>
          </form>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
