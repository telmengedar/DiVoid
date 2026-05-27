/**
 * Load-bearing tests for usePeekState (DiVoid #275 / #1253).
 *
 * Tests pin the URL-state contract from design §8.3:
 *  - peekId derived from ?peek=<id> (positive integer or null).
 *  - openPeek pushes a history entry with ?peek=id.
 *  - closePeek pushes a history entry without the peek param.
 *
 * Each test's mental-deletion check: reverting the production code that
 * each test pins causes the assertion to fail concretely.
 */

import { describe, it, expect } from 'vitest';
import { render, screen, act } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { usePeekState } from './usePeekState';

// ─── Helpers ──────────────────────────────────────────────────────────────────

/** Harness component that renders peekId and exposes open/close buttons. */
function PeekHarness() {
  const { peekId, openPeek, closePeek } = usePeekState();
  const location = useLocation();
  return (
    <div>
      <div data-testid="peek-id">{peekId === null ? 'null' : String(peekId)}</div>
      <div data-testid="search">{location.search}</div>
      <button onClick={() => openPeek(42)}>Open 42</button>
      <button onClick={() => openPeek(7)}>Open 7</button>
      <button onClick={closePeek}>Close</button>
    </div>
  );
}

function render_at(initialPath: string) {
  return render(
    <MemoryRouter initialEntries={[initialPath]}>
      <PeekHarness />
    </MemoryRouter>,
  );
}

// ─── Tests ────────────────────────────────────────────────────────────────────

describe('usePeekState — peekId derivation', () => {
  /**
   * P1: no ?peek → peekId is null.
   * Mental-deletion: if parseInt returns a value instead of NaN for absent param,
   * or if the null guard is removed, peekId would not be null.
   */
  it('P1: returns null when ?peek is absent', () => {
    render_at('/workspace');
    expect(screen.getByTestId('peek-id')).toHaveTextContent('null');
  });

  /**
   * P2: ?peek=42 → peekId is 42.
   * Mental-deletion: remove parseInt or the > 0 guard → peekId stays null.
   */
  it('P2: returns the numeric id when ?peek=42 is present', () => {
    render_at('/workspace?peek=42');
    expect(screen.getByTestId('peek-id')).toHaveTextContent('42');
  });

  /**
   * P3: ?peek=0 → peekId is null (non-positive guard).
   * Mental-deletion: remove the > 0 guard → peekId would be 0 (exposed as '0').
   */
  it('P3: returns null for ?peek=0 (non-positive)', () => {
    render_at('/workspace?peek=0');
    expect(screen.getByTestId('peek-id')).toHaveTextContent('null');
  });

  it('P4: returns null for ?peek=-5 (negative)', () => {
    render_at('/workspace?peek=-5');
    expect(screen.getByTestId('peek-id')).toHaveTextContent('null');
  });

  it('P5: returns null for ?peek=abc (non-numeric)', () => {
    render_at('/workspace?peek=abc');
    expect(screen.getByTestId('peek-id')).toHaveTextContent('null');
  });
});

describe('usePeekState — openPeek', () => {
  /**
   * O1: openPeek(42) sets ?peek=42 in URL.
   * Mental-deletion: remove the setSearchParams call or the key 'peek' →
   * URL does not contain peek=42 and peekId stays null.
   */
  it('O1: sets ?peek=42 in the URL when openPeek(42) is called', async () => {
    const user = userEvent.setup();
    render_at('/workspace');

    await user.click(screen.getByRole('button', { name: 'Open 42' }));

    expect(screen.getByTestId('search')).toHaveTextContent('peek=42');
    expect(screen.getByTestId('peek-id')).toHaveTextContent('42');
  });

  it('O2: updates to ?peek=7 when openPeek(7) is called', async () => {
    const user = userEvent.setup();
    render_at('/workspace');

    await user.click(screen.getByRole('button', { name: 'Open 7' }));

    expect(screen.getByTestId('search')).toHaveTextContent('peek=7');
    expect(screen.getByTestId('peek-id')).toHaveTextContent('7');
  });
});

describe('usePeekState — closePeek', () => {
  /**
   * C1: closePeek removes ?peek from URL.
   * Mental-deletion: change `next.delete('peek')` to a no-op →
   * ?peek stays in URL and peekId remains non-null.
   */
  it('C1: removes ?peek from URL when closePeek is called', async () => {
    const user = userEvent.setup();
    render_at('/workspace?peek=42');

    expect(screen.getByTestId('peek-id')).toHaveTextContent('42');

    await user.click(screen.getByRole('button', { name: 'Close' }));

    expect(screen.getByTestId('peek-id')).toHaveTextContent('null');
    expect(screen.getByTestId('search')).not.toHaveTextContent('peek');
  });
});

describe('usePeekState — round-trip', () => {
  /**
   * R1: open → close → open again works correctly.
   * Mental-deletion: if closePeek accidentally navigates away or corrupts state,
   * the second openPeek fails.
   */
  it('R1: open → close → open round-trips correctly', async () => {
    const user = userEvent.setup();
    render_at('/workspace');

    await user.click(screen.getByRole('button', { name: 'Open 42' }));
    expect(screen.getByTestId('peek-id')).toHaveTextContent('42');

    await act(async () => {
      await user.click(screen.getByRole('button', { name: 'Close' }));
    });
    expect(screen.getByTestId('peek-id')).toHaveTextContent('null');

    await user.click(screen.getByRole('button', { name: 'Open 7' }));
    expect(screen.getByTestId('peek-id')).toHaveTextContent('7');
  });
});
