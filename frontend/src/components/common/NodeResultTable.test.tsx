/**
 * NodeResultTable tests — load-bearing per DiVoid #275.
 *
 * Covers the Refinement column added for DiVoid #14811 (Unit C of #14806).
 *
 * Load-bearing substitution proof per contract §13.1:
 *
 *  T1 (renders a known value): revert the `<RefinementBadge refinement={node.refinement} />`
 *     cell in NodeResultTable.tsx. T1 fails — `getByText('ready')` finds nothing.
 *
 *  T2 (renders an unrecognised value verbatim — the open-vocabulary guarantee):
 *     the production component has no switch/map keyed by refinement value (unlike
 *     StatusBadge's colorMap), so there is nothing to "revert" that would special-case
 *     a known value over an unknown one. The real regression this guards is someone
 *     later adding such a map: if that happened, this value ('a-value-nobody-coined-yet')
 *     would still render literally (RefinementBadge has no lookup to fall through), so
 *     the test would keep passing — which is exactly the point of #14811's "never
 *     hard-code a member list" instruction. Falsifier: introduce a colorMap keyed by
 *     known refinement strings with a `?? null` default for unrecognised ones — T2 still
 *     passes today because no such map exists; it is the regression trip-wire for one.
 *
 *  T3 (renders em-dash for null): revert the `if (!refinement) return <span>—</span>`
 *     guard in RefinementBadge.tsx. T3 fails — the refinement cell (5th <td>, checked
 *     by index specifically because the Status column *also* renders "—" for a null
 *     status and would otherwise mask this exact regression) goes from "—" to empty.
 */

import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { NodeResultTable } from './NodeResultTable';
import type { NodeDetails } from '@/types/divoid';

function renderTable(nodes: NodeDetails[]) {
  return render(
    <MemoryRouter>
      <NodeResultTable nodes={nodes} />
    </MemoryRouter>,
  );
}

describe('NodeResultTable — Refinement column (DiVoid #14811)', () => {
  it('renders the Refinement column header', () => {
    renderTable([]);
    expect(screen.getByRole('columnheader', { name: 'Refinement' })).toBeInTheDocument();
  });

  it('T1: renders a known refinement value verbatim', () => {
    const node: NodeDetails = { id: 1, type: 'task', name: 'Ready task', status: 'open', refinement: 'ready' };
    renderTable([node]);
    expect(screen.getByText('ready')).toBeInTheDocument();
  });

  it('T2: renders a value nobody anticipated verbatim (open vocabulary, no allow-list)', () => {
    const node: NodeDetails = {
      id: 2,
      type: 'task',
      name: 'Novel refinement',
      status: 'open',
      refinement: 'a-value-nobody-coined-yet',
    };
    renderTable([node]);
    expect(screen.getByText('a-value-nobody-coined-yet')).toBeInTheDocument();
  });

  it('T3: renders an em-dash in the Refinement cell when refinement is null', () => {
    // status is non-null so the Status cell's own "—" fallback can't mask a
    // regression in the Refinement cell specifically.
    const node: NodeDetails = { id: 3, type: 'documentation', name: 'Unclassified doc', status: 'open', refinement: null };
    renderTable([node]);
    const row = screen.getByText('Unclassified doc').closest('tr');
    expect(row).not.toBeNull();
    const refinementCell = row!.querySelectorAll('td')[4];
    expect(refinementCell.textContent).toBe('—');
  });

  it('T3b: renders an em-dash in the Refinement cell when refinement is absent (undefined, pre-adoption default)', () => {
    const node: NodeDetails = { id: 4, type: 'project', name: 'Old node', status: 'open' };
    renderTable([node]);
    const row = screen.getByText('Old node').closest('tr');
    expect(row).not.toBeNull();
    const refinementCell = row!.querySelectorAll('td')[4];
    expect(refinementCell.textContent).toBe('—');
  });
});
