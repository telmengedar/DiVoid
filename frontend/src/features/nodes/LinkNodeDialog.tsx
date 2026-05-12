/**
 * LinkNodeDialog — "Add link" dialog on the node detail page.
 *
 * Uses semantic search (useNodeSemantic) as the node picker.
 * On confirmation: calls useLinkNodes to POST the link; the linkedto query
 * is invalidated by the mutation hook so the neighbours list updates.
 *
 * Design: docs/architecture/frontend-bootstrap.md §5.6, §6.6
 * Task: DiVoid node #229
 */

import { useState, useEffect } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import * as Dialog from '@radix-ui/react-dialog';
import { X, Search, Link2 } from 'lucide-react';
import { useLinkNodes } from './mutations';
import { useNodeSemantic } from './useNodeSemantic';
import { linkSearchSchema, type LinkSearchFormValues } from './schemas';
import type { NodeDetails } from '@/types/divoid';
import { ROUTES } from '@/lib/constants';

interface LinkNodeDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  sourceId: number;
}

export function LinkNodeDialog({ open, onOpenChange, sourceId }: LinkNodeDialogProps) {
  const mutation = useLinkNodes();
  const [query, setQuery] = useState('');
  const [selected, setSelected] = useState<NodeDetails | null>(null);

  const { data: searchResults, isFetching: isSearching } = useNodeSemantic(query);

  const {
    register,
    handleSubmit,
    reset,
    formState: { errors },
  } = useForm<LinkSearchFormValues>({
    resolver: zodResolver(linkSearchSchema),
    defaultValues: { query: '' },
  });

  // Reset state when dialog closes.
  useEffect(() => {
    if (!open) {
      reset();
      setQuery('');
      setSelected(null);
      mutation.reset();
    }
  }, [open, reset, mutation]);

  const handleSearch = handleSubmit(({ query: q }) => {
    setQuery(q.trim());
    setSelected(null);
  });

  const handleConfirm = async () => {
    if (!selected) return;
    try {
      await mutation.mutateAsync({ sourceId, targetId: selected.id });
      onOpenChange(false);
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
          className="fixed z-50 left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 w-full max-w-lg bg-background border border-border rounded-lg shadow-lg p-6 focus:outline-none data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 data-[state=closed]:zoom-out-95 data-[state=open]:zoom-in-95"
          aria-describedby="link-node-description"
        >
          <div className="flex items-center justify-between mb-4">
            <Dialog.Title className="text-base font-semibold">Add link</Dialog.Title>
            <Dialog.Close asChild>
              <button
                aria-label="Close dialog"
                className="rounded-md p-1 text-muted-foreground hover:text-foreground hover:bg-muted transition-colors"
              >
                <X size={16} aria-hidden="true" />
              </button>
            </Dialog.Close>
          </div>

          <p id="link-node-description" className="text-sm text-muted-foreground mb-4">
            Search for a node to link to, then confirm.
          </p>

          {/* Search form */}
          <form onSubmit={handleSearch} noValidate className="flex gap-2 mb-4">
            <input
              type="search"
              placeholder="Search nodes…"
              autoComplete="off"
              className="flex-1 h-9 rounded-md border border-border bg-background px-3 text-sm placeholder:text-muted-foreground focus:outline-none focus:ring-2 focus:ring-ring"
              aria-label="Search for a node to link"
              aria-invalid={!!errors.query}
              {...register('query')}
            />
            <button
              type="submit"
              disabled={isSearching}
              className="inline-flex items-center gap-1.5 h-9 px-4 rounded-md bg-primary text-primary-foreground text-sm font-medium hover:opacity-90 disabled:opacity-50 disabled:cursor-not-allowed transition-opacity"
            >
              <Search size={14} aria-hidden="true" />
              Search
            </button>
          </form>

          {/* Results list */}
          {searchResults && searchResults.result.length > 0 && (
            <div
              className="border border-border rounded-md divide-y divide-border max-h-52 overflow-y-auto mb-4"
              role="listbox"
              aria-label="Search results — click to select"
            >
              {searchResults.result
                .filter((n) => n.id !== sourceId)
                .map((n) => (
                  <button
                    key={n.id}
                    type="button"
                    role="option"
                    aria-selected={selected?.id === n.id}
                    onClick={() => setSelected(n)}
                    className={`w-full flex items-start gap-3 px-3 py-2 text-left text-sm transition-colors hover:bg-muted ${
                      selected?.id === n.id ? 'bg-primary/10 text-primary' : ''
                    }`}
                  >
                    <span className="shrink-0 tabular-nums text-muted-foreground w-8">{n.id}</span>
                    <span className="flex-1 min-w-0">
                      <span className="block font-medium truncate">{n.name}</span>
                      <span className="text-xs text-muted-foreground font-mono">{n.type}</span>
                    </span>
                    {n.similarity !== undefined && (
                      <span className="shrink-0 text-xs text-muted-foreground tabular-nums">
                        {(n.similarity * 100).toFixed(0)}%
                      </span>
                    )}
                  </button>
                ))}
            </div>
          )}

          {query && searchResults?.result.length === 0 && !isSearching && (
            <p className="text-sm text-muted-foreground mb-4">No results for "{query}".</p>
          )}

          {/* Selected preview */}
          {selected && (
            <div className="rounded-md bg-muted/40 border border-border px-3 py-2 mb-4 text-sm flex items-center gap-2">
              <Link2 size={14} className="text-primary shrink-0" aria-hidden="true" />
              <span className="text-muted-foreground">Link to:</span>
              <a
                href={ROUTES.NODE_DETAIL(selected.id)}
                className="font-medium hover:underline"
                target="_blank"
                rel="noreferrer"
              >
                {selected.name}
              </a>
              <span className="text-xs font-mono text-muted-foreground ml-auto">#{selected.id}</span>
            </div>
          )}

          {/* Actions */}
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
              disabled={!selected || mutation.isPending}
              className="inline-flex items-center gap-1.5 h-9 px-4 rounded-md bg-primary text-primary-foreground text-sm font-medium hover:opacity-90 disabled:opacity-50 disabled:cursor-not-allowed transition-opacity"
            >
              <Link2 size={14} aria-hidden="true" />
              {mutation.isPending ? 'Linking…' : 'Link'}
            </button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
