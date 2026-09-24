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
 *  T2 (open vocabulary — no allow-list, contract §13.3(b)): a known value and an
 *     unrecognised one must render with an identical `className`. Introduce a
 *     `colorMap` keyed by known refinement strings (the exact `StatusBadge` shape
 *     #14811 forbids) and the two classNames diverge — T2 fails with a concrete
 *     mismatch instead of passing regardless of the regression.
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

  it('T2: a known and an unrecognised refinement value render with an identical className (open vocabulary, no allow-list)', () => {
    const known: NodeDetails = { id: 2, type: 'task', name: 'Known refinement', status: 'open', refinement: 'ready' };
    const unknown: NodeDetails = {
      id: 3,
      type: 'task',
      name: 'Novel refinement',
      status: 'open',
      refinement: 'a-value-nobody-coined-yet',
    };
    renderTable([known, unknown]);
    const knownBadge = screen.getByText('ready');
    const unknownBadge = screen.getByText('a-value-nobody-coined-yet');
    expect(unknownBadge.className).toBe(knownBadge.className);
  });

  it('T3: renders an em-dash in the Refinement cell when refinement is null', () => {
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
