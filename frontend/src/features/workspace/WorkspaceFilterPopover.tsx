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
 * Task: DiVoid node #318
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
              return (
                <li key={opt.value}>
                  <label
                    className={cn(
                      'flex cursor-pointer items-center gap-2 rounded px-1 py-1 text-sm',
                      'hover:bg-muted transition-colors',
                    )}
                    htmlFor={`filter-opt-${opt.value}`}
                  >
                    <Checkbox.Root
                      id={`filter-opt-${opt.value}`}
                      checked={checked}
                      onCheckedChange={() => onToggle(opt.value)}
                      className={cn(
                        'flex h-4 w-4 shrink-0 items-center justify-center rounded border border-input',
                        'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
                        checked
                          ? 'bg-primary border-primary text-primary-foreground'
                          : 'bg-background',
                      )}
                      aria-label={opt.label}
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
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}
