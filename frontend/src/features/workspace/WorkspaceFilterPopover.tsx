/**
 * WorkspaceFilterPopover — reusable popover trigger + checkbox list for
 * workspace canvas filters (type filter and status filter).
 *
 * Uses Radix Popover + Checkbox primitives. Theme-aware via Radix's class
 * inheritance (next-themes sets the `dark` class on <html>).
 *
 * ## Props
 *  - label:     trigger button text (e.g. "Type", "Status")
 *  - options:   array of { value, label } options to render as checkboxes
 *  - selected:  currently selected values (Set)
 *  - onToggle:  callback when a checkbox is toggled
 *  - active:    when true, shows a selected-count badge on the trigger
 *
 * ## Last-selected prevention (DiVoid #1981) — the affordance, not the invariant
 *
 * This component can refuse a *transition* (unchecking the last selected
 * option); it cannot repair a *state* it did not create (e.g. a value
 * hydrated from stale storage that is already empty). So this guard only
 * fires when exactly one option is currently selected and the user tries to
 * uncheck it — at that point the checkbox is marked `aria-disabled` (it
 * stays focusable) and the toggle is swallowed instead of forwarded, and an
 * inline hint explains why. At zero selected, nothing here claims a
 * constraint the component isn't enforcing: no checkbox is marked
 * `aria-disabled` (none of them are checked, so none are "the last one")
 * and the hint does not render. The actual floor for the zero-selected case
 * is enforced one layer down, in `useNodesInViewport`'s `enabled` gate —
 * see that file for DiVoid #1981 CF-1.
 *
 * Applies uniformly to both the type and status popovers since they share
 * this component.
 *
 * Task: DiVoid node #318 / #1981
 */

import * as Popover from '@radix-ui/react-popover';
import * as Checkbox from '@radix-ui/react-checkbox';
import { Check, SlidersHorizontal } from 'lucide-react';
import { cn } from '@/lib/cn';

export interface FilterOption {
  value: string;
  label: string;
}

interface WorkspaceFilterPopoverProps {
  /** Trigger button label */
  label: string;
  /** Full list of available options */
  options: FilterOption[];
  /** Currently selected values */
  selected: Set<string>;
  /** Called when a checkbox is toggled */
  onToggle: (value: string) => void;
  /**
   * When true, shows a badge indicating the filter deviates from its default
   * state. Badge text is the count of selected options.
   */
  active: boolean;
}

export function WorkspaceFilterPopover({
  label,
  options,
  selected,
  onToggle,
  active,
}: WorkspaceFilterPopoverProps) {
  const selectedCount = selected.size;
  const atFloor = selectedCount === 1;

  return (
    <Popover.Root>
      <Popover.Trigger asChild>
        <button
          className={cn(
            'relative inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-xs font-medium transition-colors',
            'border-border bg-background text-foreground',
            'hover:bg-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
            active && 'border-primary',
          )}
          aria-label={`${label} filter — ${selectedCount} of ${options.length} selected`}
          aria-haspopup="dialog"
        >
          <SlidersHorizontal size={12} aria-hidden="true" />
          {label}
          {active && (
            <span
              className="ml-0.5 inline-flex h-4 w-4 items-center justify-center rounded-full bg-primary text-primary-foreground text-[10px] font-semibold"
              aria-label={`${selectedCount} selected`}
            >
              {selectedCount}
            </span>
          )}
        </button>
      </Popover.Trigger>

      <Popover.Portal>
        <Popover.Content
          sideOffset={6}
          align="start"
          className={cn(
            'z-50 min-w-44 rounded-md border border-border bg-popover p-2 shadow-md',
            'text-sm text-popover-foreground',
            // Radix animate-in / animate-out via CSS data attributes:
            'data-[state=open]:animate-in data-[state=open]:fade-in-0 data-[state=open]:zoom-in-95',
            'data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=closed]:zoom-out-95',
          )}
          role="dialog"
          aria-label={`${label} filter options`}
        >
          <p className="px-1 pb-1.5 text-[11px] font-medium text-muted-foreground uppercase tracking-wide">
            {label}
          </p>
          <ul role="list" className="space-y-0.5">
            {options.map((opt) => {
              const checked = selected.has(opt.value);
              const isLastSelected = checked && atFloor;
              return (
                <li key={opt.value}>
                  <label
                    className={cn(
                      'flex items-center gap-2 rounded px-1 py-1 text-sm transition-colors',
                      isLastSelected
                        ? 'cursor-not-allowed opacity-60'
                        : 'cursor-pointer hover:bg-muted',
                    )}
                    htmlFor={`filter-opt-${opt.value}`}
                  >
                    <Checkbox.Root
                      id={`filter-opt-${opt.value}`}
                      checked={checked}
                      aria-disabled={isLastSelected}
                      onCheckedChange={() => {
                        if (isLastSelected) return;
                        onToggle(opt.value);
                      }}
                      className={cn(
                        'flex h-4 w-4 shrink-0 items-center justify-center rounded border border-input',
                        'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
                        checked
                          ? 'bg-primary border-primary text-primary-foreground'
                          : 'bg-background',
                        isLastSelected && 'cursor-not-allowed',
                      )}
                      aria-label={
                        isLastSelected
                          ? `${opt.label} — at least one ${label.toLowerCase()} option must remain selected`
                          : opt.label
                      }
                    >
                      <Checkbox.Indicator className="flex items-center justify-center">
                        <Check size={10} strokeWidth={3} aria-hidden="true" />
                      </Checkbox.Indicator>
                    </Checkbox.Root>
                    <span className="select-none">{opt.label}</span>
                  </label>
                </li>
              );
            })}
          </ul>
          {atFloor && (
            <p
              role="status"
              aria-live="polite"
              className="mt-1.5 px-1 text-[11px] text-muted-foreground"
            >
              At least one {label.toLowerCase()} option must stay selected.
            </p>
          )}
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}
