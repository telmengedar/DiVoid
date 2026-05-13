/**
 * CreateTaskDialog — creates a new task pre-linked to the project's Tasks group.
 *
 * Pre-populates:
 *  - type = "task"
 *  - status = "new" (human quick-capture status per DiVoid node #9)
 *  - On success: links the new node to tasksGroupId via POST /nodes/{id}/links
 *
 * If the project has no Tasks group (tasksGroupId is undefined), the dialog
 * renders a blocking message and disables the create flow. The user must
 * create the Tasks group in the workspace first.
 *
 * Error handling:
 *  - Create errors: sonner toast from useCreateNode (already handled by hook).
 *  - Link errors: bug #317 already-linked graceful path in useLinkNodes.
 *
 * Task: DiVoid node #369
 */

import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import * as Dialog from '@radix-ui/react-dialog';
import { X } from 'lucide-react';
import { z } from 'zod';
import { useCreateNode } from '@/features/nodes/mutations';
import { useLinkNodes } from '@/features/nodes/mutations';
import { TASK_STATUSES } from '@/features/nodes/schemas';

// ─── Schema ───────────────────────────────────────────────────────────────────

const createTaskSchema = z.object({
  name: z.string().min(1, 'Name is required').max(255, 'Name must be 255 characters or fewer'),
  status: z.enum(TASK_STATUSES),
});

type CreateTaskFormValues = z.infer<typeof createTaskSchema>;

// ─── Component ────────────────────────────────────────────────────────────────

interface CreateTaskDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** The Tasks group node id to link the new task to. */
  tasksGroupId: number | undefined;
  /** Called after the node is created and linked. */
  onCreated?: (id: number) => void;
}

export function CreateTaskDialog({
  open,
  onOpenChange,
  tasksGroupId,
  onCreated,
}: CreateTaskDialogProps) {
  const createMutation = useCreateNode();
  const linkMutation = useLinkNodes();

  const {
    register,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<CreateTaskFormValues>({
    resolver: zodResolver(createTaskSchema),
    defaultValues: { name: '', status: 'new' },
  });

  const handleOpenChange = (isOpen: boolean) => {
    if (!isOpen) {
      reset();
      createMutation.reset();
      linkMutation.reset();
    }
    onOpenChange(isOpen);
  };

  const onSubmit = handleSubmit(async (values) => {
    try {
      const node = await createMutation.mutateAsync({
        type: 'task',
        name: values.name.trim(),
        status: values.status,
      });

      if (tasksGroupId !== undefined) {
        // Link new task to the project's Tasks group.
        // useLinkNodes already handles the bug-#317 already-linked 500 gracefully.
        await linkMutation.mutateAsync({ sourceId: node.id, targetId: tasksGroupId });
      }

      onCreated?.(node.id);
      handleOpenChange(false);
    } catch {
      // Errors toasted by mutation hooks — keep dialog open for retry.
    }
  });

  const isPending = isSubmitting || createMutation.isPending || linkMutation.isPending;

  return (
    <Dialog.Root open={open} onOpenChange={handleOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-black/40 z-40 data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0" />
        <Dialog.Content
          className="fixed z-50 left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 w-full max-w-md bg-background border border-border rounded-lg shadow-lg p-6 focus:outline-none data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 data-[state=closed]:zoom-out-95 data-[state=open]:zoom-in-95"
          aria-describedby="create-task-description"
        >
          <div className="flex items-center justify-between mb-4">
            <Dialog.Title className="text-base font-semibold">New task</Dialog.Title>
            <Dialog.Close asChild>
              <button
                aria-label="Close dialog"
                className="rounded-md p-1 text-muted-foreground hover:text-foreground hover:bg-muted transition-colors"
              >
                <X size={16} aria-hidden="true" />
              </button>
            </Dialog.Close>
          </div>

          <p id="create-task-description" className="sr-only">
            Create a new task node linked to this project&apos;s Tasks group.
          </p>

          {/* Missing Tasks group — blocking message */}
          {tasksGroupId === undefined ? (
            <div
              role="alert"
              className="rounded-md border border-amber-300/60 bg-amber-50/80 dark:bg-amber-900/20 dark:border-amber-700/40 px-4 py-3 text-sm text-amber-800 dark:text-amber-300"
              data-testid="no-tasks-group-message"
            >
              This project has no <code className="font-mono">Tasks</code> group node. Create one in
              the workspace first.
            </div>
          ) : (
            <form onSubmit={onSubmit} noValidate className="flex flex-col gap-4">
              {/* Name */}
              <div className="flex flex-col gap-1">
                <label htmlFor="create-task-name" className="text-sm font-medium">
                  Name <span aria-hidden="true">*</span>
                </label>
                <input
                  id="create-task-name"
                  type="text"
                  placeholder="What needs to be done?"
                  autoComplete="off"
                  className="h-9 rounded-md border border-border bg-background px-3 text-sm placeholder:text-muted-foreground focus:outline-none focus:ring-2 focus:ring-ring disabled:opacity-50"
                  aria-invalid={!!errors.name}
                  aria-describedby={errors.name ? 'create-task-name-error' : undefined}
                  {...register('name')}
                />
                {errors.name && (
                  <p id="create-task-name-error" className="text-xs text-destructive" role="alert">
                    {errors.name.message}
                  </p>
                )}
              </div>

              {/* Status */}
              <div className="flex flex-col gap-1">
                <label htmlFor="create-task-status" className="text-sm font-medium">
                  Status
                </label>
                <select
                  id="create-task-status"
                  className="h-9 rounded-md border border-border bg-background px-3 text-sm focus:outline-none focus:ring-2 focus:ring-ring disabled:opacity-50"
                  {...register('status')}
                >
                  {TASK_STATUSES.map((s) => (
                    <option key={s} value={s}>
                      {s}
                    </option>
                  ))}
                </select>
              </div>

              {/* Type is pre-fixed to "task" — shown as read-only info */}
              <p className="text-xs text-muted-foreground">
                Type: <span className="font-mono rounded bg-muted px-1 py-0.5">task</span>
              </p>

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
                  disabled={isPending}
                  className="h-9 px-4 rounded-md bg-primary text-primary-foreground text-sm font-medium hover:opacity-90 disabled:opacity-50 disabled:cursor-not-allowed transition-opacity"
                >
                  {isPending ? 'Creating…' : 'Create task'}
                </button>
              </div>
            </form>
          )}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
