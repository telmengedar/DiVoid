// @vitest-environment happy-dom
/**
 * Load-bearing tests for WorkspaceFilterPopover's last-selected prevention
 * (DiVoid #1981, #275, #420 §13). Revised per Jenny QA review #10854 (CF-1,
 * W2, W3): the floor for the fully-empty state now lives in
 * useNodesInViewport's `enabled` gate (see useNodesInViewport.enabledGate.test.tsx)
 * — this file only pins the component-level affordance and its own boundary.
 *
 * Background: see DiVoid #1981 (and #1976, the spec defect that exposed it)
 * for the backend filter-semantics defect this UI affordance closes the
 * frontend half of — not re-derived here.
 *
 * Fix (approach 1 from #1981): once only one option remains selected, this
 * component marks that option's checkbox `aria-disabled` (kept focusable —
 * #10854 W2) and swallows the toggle instead of forwarding it to the caller.
 *
 * Test 1 (positive): with two options selected, toggling one of them still
 * calls onToggle — the prevention must not interfere with ordinary toggles.
 * Test 2 (real substitution, not tautological per §13.3): with exactly one
 * option selected, its checkbox is aria-disabled but still focusable,
 * clicking it does not call onToggle, and the inline hint is rendered.
 * Reverting the `isLastSelected` guard re-enables the toggle and removes the
 * hint — all assertions fail.
 * Test 3 (CF-1 regression): with zero options selected, the component does
 * not claim a constraint it isn't enforcing — no checkbox is aria-disabled
 * and the hint does not render. Reverting the `atFloor` fix from `=== 1`
 * back to `<= 1` makes the hint render at zero — this assertion fails.
 * Test 4 (W3): the same second-to-last / last-selected behaviour holds for
 * a `label="Status"` instantiation, not just "Type".
 */

import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { WorkspaceFilterPopover, type FilterOption } from './WorkspaceFilterPopover';

const OPTIONS: FilterOption[] = [
  { value: 'a', label: 'Alpha' },
  { value: 'b', label: 'Beta' },
];

const STATUS_OPTIONS: FilterOption[] = [
  { value: 'open', label: 'Open' },
  { value: 'closed', label: 'Closed' },
];

async function openPopover(filterLabelPattern: RegExp) {
  fireEvent.click(screen.getByRole('button', { name: filterLabelPattern }));
  await waitFor(() => {
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });
}

describe('WorkspaceFilterPopover — last-selected prevention (DiVoid #1981)', () => {
  it('toggling the second-to-last selected option calls onToggle', async () => {
    const onToggle = vi.fn();
    render(
      <WorkspaceFilterPopover
        label="Type"
        options={OPTIONS}
        selected={new Set(['a', 'b'])}
        onToggle={onToggle}
        active={false}
      />,
    );

    await openPopover(/type filter/i);

    const alpha = screen.getByRole('checkbox', { name: /^Alpha$/ });
    expect(alpha).toHaveAttribute('aria-disabled', 'false');

    fireEvent.click(alpha);

    expect(onToggle).toHaveBeenCalledTimes(1);
    expect(onToggle).toHaveBeenCalledWith('a');
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });

  it('toggling the last selected option does not call onToggle, stays focusable, and surfaces the hint', async () => {
    const onToggle = vi.fn();
    render(
      <WorkspaceFilterPopover
        label="Type"
        options={OPTIONS}
        selected={new Set(['a'])}
        onToggle={onToggle}
        active={false}
      />,
    );

    await openPopover(/type filter/i);

    const alpha = screen.getByRole('checkbox', { name: /Alpha/ });
    expect(alpha).toHaveAttribute('aria-disabled', 'true');

    alpha.focus();
    expect(document.activeElement).toBe(alpha);

    fireEvent.click(alpha);

    expect(onToggle).not.toHaveBeenCalled();
    expect(screen.getByRole('status')).toHaveTextContent(
      /at least one type option must stay selected/i,
    );

    const beta = screen.getByRole('checkbox', { name: /^Beta$/ });
    expect(beta).toHaveAttribute('aria-disabled', 'false');
  });

  it('at zero selected, no checkbox is marked aria-disabled and the hint does not render (DiVoid #10854 CF-1)', async () => {
    const onToggle = vi.fn();
    render(
      <WorkspaceFilterPopover
        label="Type"
        options={OPTIONS}
        selected={new Set()}
        onToggle={onToggle}
        active={false}
      />,
    );

    await openPopover(/type filter/i);

    expect(screen.getByRole('checkbox', { name: /^Alpha$/ })).toHaveAttribute(
      'aria-disabled',
      'false',
    );
    expect(screen.getByRole('checkbox', { name: /^Beta$/ })).toHaveAttribute(
      'aria-disabled',
      'false',
    );
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });

  it('the same last-selected prevention holds for a Status instantiation (DiVoid #10854 W3)', async () => {
    const onToggle = vi.fn();
    render(
      <WorkspaceFilterPopover
        label="Status"
        options={STATUS_OPTIONS}
        selected={new Set(['open'])}
        onToggle={onToggle}
        active={false}
      />,
    );

    await openPopover(/status filter/i);

    const open = screen.getByRole('checkbox', { name: /open/i });
    expect(open).toHaveAttribute('aria-disabled', 'true');

    fireEvent.click(open);

    expect(onToggle).not.toHaveBeenCalled();
    expect(screen.getByRole('status')).toHaveTextContent(
      /at least one status option must stay selected/i,
    );
  });
});
