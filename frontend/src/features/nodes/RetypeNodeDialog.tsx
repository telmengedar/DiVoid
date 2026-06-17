/**
 * RetypeNodeDialog — change a node's type via PATCH /api/nodes/{id}.
 *
 * Presents a datalist-backed text input listing all known types plus an
 * "untyped" option. On submit it issues a JSON-Patch replace on /type:
 *   { op: 'replace', path: '/type', value: typeName | '' }
 * An empty string signals "untyped" — the backend normalises null/empty/
 * whitespace to the untyped row (design #2014 §4 decision 2).
 *
 * Soft warning (design #2014 §9.1): fires when the node currently has
 * a status value AND the target type is outside the lifecycle-bearing
 * allowlist (task, bug). The warning is advisory; the user may proceed.
 *
 * Design: docs/architecture/node-type-untyped-and-retype.md §9.1
 * Task: DiVoid node #2012
 * Backend PR: #149
 */

import { useEffect, useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import * as Dialog from '@radix-ui/react-dialog';
import { TriangleAlert, X } from 'lucide-react';
import { z } from 'zod';
import { usePatchNode } from './mutations';
import { UNTYPED_VALUE } from '@/features/workspace/useWorkspaceFilters';
import { useNodeTypes } from '@/features/workspace/useNodeTypes';
import type { NodeDetails } from '@/types/divoid';

// ─── Warning heuristic (design #2014 §9.1) ───────────────────────────────────

/** Types whose lifecycle (status/severity) is meaningful and well-defined. */
export const LIFECYCLE_BEARING_TYPES: readonly string[] = ['task', 'bug'] as const;

/**
 * Returns true when the node has lifecycle state (a non-null status) AND the
 * target type is outside the lifecycle-bearing allowlist.
 *
 * Pure membership + presence check — no aggregation, no backend call.
 * The backend allows ALL transitions regardless of this result.
 */
export function shouldWarnOnRetype(node: NodeDetails, targetTypeName: string): boolean {
  const hasLifecycleState = node.status != null;
  const targetBearsCycle = LIFECYCLE_BEARING_TYPES.includes(targetTypeName);
  return hasLifecycleState && !targetBearsCycle;
}

// ─── Form schema ──────────────────────────────────────────────────────────────

const retypeSchema = z.object({
  type: z.string(),
});

type RetypeFormValues = z.infer<typeof retypeSchema>;

/** Display label shown in the datalist for the "untyped" option. */
export const UNTYPED_DISPLAY = '(untyped)';

/**
 * Converts the form value to the string sent in the PATCH op.
 * UNTYPED_DISPLAY and UNTYPED_VALUE both map to '' (backend normalises to null).
 */
export function toApiValue(formValue: string): string {
  const trimmed = formValue.trim();
  if (trimmed === UNTYPED_DISPLAY || trimmed === UNTYPED_VALUE) return '';
  return trimmed;
}

// ─── Component ────────────────────────────────────────────────────────────────

interface RetypeNodeDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  node: NodeDetails;
}

/**
 * RetypeNodeDialog — lets the user change an existing node's type.
 *
 * Issues PATCH /api/nodes/{id} with { op:'replace', path:'/type', value } where
 * value is the new type name (empty string = untyped).
 *
 * Shows a soft, non-blocking warning when the node carries lifecycle state
 * (status set) and the target type is not in LIFECYCLE_BEARING_TYPES.
 */
export function RetypeNodeDialog({ open, onOpenChange, node }: RetypeNodeDialogProps) {
  const mutation = usePatchNode(node.id);
  const { options: typeOptions } = useNodeTypes();

  const [showWarning, setShowWarning] = useState(false);

  const {
    register,
    handleSubmit,
    watch,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<RetypeFormValues>({
    resolver: zodResolver(retypeSchema),
    defaultValues: { type: node.type ?? UNTYPED_DISPLAY },
  });

  const typeValue = watch('type');

  useEffect(() => {
    if (open) {
      reset({ type: node.type ?? UNTYPED_DISPLAY });
      setShowWarning(false);
    }
  }, [open, node.type, reset]);

  useEffect(() => {
    const apiValue = toApiValue(typeValue);
    setShowWarning(shouldWarnOnRetype(node, apiValue));
  }, [typeValue, node]);

  const handleOpenChange = (isOpen: boolean) => {
    if (!isOpen) {
      mutation.reset();
      setShowWarning(false);
    }
    onOpenChange(isOpen);
  };

  const onSubmit = handleSubmit(async (values) => {
    const apiValue = toApiValue(values.type);
    const currentApiValue = node.type ?? '';

    if (apiValue === currentApiValue) {
      handleOpenChange(false);
      return;
    }

    try {
      await mutation.mutateAsync([
        { op: 'replace', path: '/type', value: apiValue },
      ]);
      handleOpenChange(false);
    } catch {
      // Error toast shown by the mutation's onError handler.
    }
  });

  const datalistId = 'retype-type-suggestions';

  return (
    <Dialog.Root open={open} onOpenChange={handleOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-black/40 z-40 data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0" />
        <Dialog.Content
          className="fixed z-50 left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 w-full max-w-md bg-background border border-border rounded-lg shadow-lg p-6 focus:outline-none data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 data-[state=closed]:zoom-out-95 data-[state=open]:zoom-in-95"
          aria-describedby="retype-node-description"
        >
          <div className="flex items-center justify-between mb-4">
            <Dialog.Title className="text-base font-semibold">Change node type</Dialog.Title>
            <Dialog.Close asChild>
              <button
                aria-label="Close dialog"
                className="rounded-md p-1 text-muted-foreground hover:text-foreground hover:bg-muted transition-colors"
              >
                <X size={16} aria-hidden="true" />
              </button>
            </Dialog.Close>
          </div>

          <p id="retype-node-description" className="sr-only">
            Change the type of this node. You can pick an existing type or enter a new one.
          </p>

          <form onSubmit={onSubmit} noValidate className="flex flex-col gap-4">
            <div className="flex flex-col gap-1">
              <label htmlFor="retype-type" className="text-sm font-medium">
                Type
              </label>
              <input
                id="retype-type"
                type="text"
                list={datalistId}
                autoComplete="off"
                placeholder={`Current: ${node.type ?? '(untyped)'}`}
                className="h-9 rounded-md border border-border bg-background px-3 text-sm placeholder:text-muted-foreground focus:outline-none focus:ring-2 focus:ring-ring disabled:opacity-50"
                aria-invalid={!!errors.type}
                aria-describedby={errors.type ? 'retype-type-error' : undefined}
                {...register('type')}
              />
              <datalist id={datalistId}>
                <option value={UNTYPED_DISPLAY} />
                {typeOptions
                  .filter((o) => o.value !== UNTYPED_VALUE)
                  .map((o) => (
                    <option key={o.value} value={o.value} />
                  ))}
              </datalist>
              {errors.type && (
                <p id="retype-type-error" className="text-xs text-destructive" role="alert">
                  {errors.type.message}
                </p>
              )}
            </div>

            {showWarning && (
              <div
                role="alert"
                className="flex items-start gap-2 rounded-md border border-amber-400/60 bg-amber-50 dark:bg-amber-900/20 px-3 py-2 text-sm text-amber-800 dark:text-amber-200"
              >
                <TriangleAlert size={16} className="mt-0.5 shrink-0" aria-hidden="true" />
                <span>
                  This node has a status value but the target type does not track lifecycle
                  state. The retype will still work — the status field will remain set.
                </span>
              </div>
            )}

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
                {mutation.isPending ? 'Saving…' : 'Change type'}
              </button>
            </div>
          </form>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
