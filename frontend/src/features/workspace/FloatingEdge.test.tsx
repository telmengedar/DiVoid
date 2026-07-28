// @vitest-environment happy-dom
/**
 * Load-bearing tests for FloatingEdge's marker forwarding + context label
 * (DiVoid #7142 PR2 / #275).
 *
 * `useInternalNode` is mocked because happy-dom/jsdom never populates the
 * ResizeObserver-driven `measured` dimensions xyflow needs to draw an edge —
 * the existing geometry tests (WorkspaceConnectDisconnect.test.tsx Test 5)
 * sidestep the same gap by testing `getIntersectionPoint` directly rather
 * than mounting a real edge. `BaseEdge`/`EdgeLabelRenderer` are mocked to
 * capture the props FloatingEdge passes them — EdgeLabelRenderer's real
 * implementation portals into a DOM node that only exists inside a
 * fully-mounted <ReactFlow>, which the measurement gap above makes moot here
 * anyway. Geometry (getIntersectionPoint/getEdgeParams) is unchanged and
 * already covered elsewhere — not re-tested in this file.
 */

import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { Position, type EdgeLabelRendererProps } from '@xyflow/react';

const mockUseInternalNode = vi.fn();

vi.mock('@xyflow/react', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@xyflow/react')>();
  return {
    ...actual,
    useInternalNode: (id: string) => mockUseInternalNode(id),
    BaseEdge: (props: Record<string, unknown>) => (
      <div
        data-testid="base-edge"
        data-marker-start={props.markerStart ? String(props.markerStart) : ''}
        data-marker-end={props.markerEnd ? String(props.markerEnd) : ''}
      />
    ),
    EdgeLabelRenderer: ({ children }: EdgeLabelRendererProps) => (
      <div data-testid="edge-label-renderer">{children}</div>
    ),
  };
});

function mockMeasuredNode(x: number, y: number, width = 100, height = 40) {
  return {
    measured: { width, height },
    internals: { positionAbsolute: { x, y } },
  };
}

const basePositionProps = {
  sourceX: 0,
  sourceY: 0,
  targetX: 0,
  targetY: 0,
  sourcePosition: Position.Right,
  targetPosition: Position.Left,
} as const;

describe('FloatingEdge', () => {
  /**
   * POSITIVE PROOF: markerStart/markerEnd props are forwarded to BaseEdge
   * unchanged — this is the whole marker-rendering contract, since the
   * marker *choice* (linkType → marker config) is computed upstream by
   * markersForLinkType, not inside FloatingEdge.
   *
   * Mental-deletion: drop `markerStart={markerStart}` from the <BaseEdge>
   * JSX → the mock's `data-marker-start` attribute is empty → the assertion
   * fails.
   */
  it('forwards markerStart and markerEnd through to BaseEdge unchanged', async () => {
    mockUseInternalNode.mockImplementation((id: string) =>
      id === 'a' ? mockMeasuredNode(0, 0) : mockMeasuredNode(200, 0),
    );
    const { FloatingEdge } = await import('./FloatingEdge');

    render(
      <FloatingEdge
        id="1-2"
        source="a"
        target="b"
        markerStart="url(#arrow-start)"
        markerEnd="url(#arrow-end)"
        {...basePositionProps}
      />,
    );

    const baseEdge = screen.getByTestId('base-edge');
    expect(baseEdge.dataset.markerStart).toBe('url(#arrow-start)');
    expect(baseEdge.dataset.markerEnd).toBe('url(#arrow-end)');
  });

  /**
   * POSITIVE PROOF: a non-empty `data.context` renders the label text inside
   * EdgeLabelRenderer.
   *
   * Mental-deletion: change the guard from `data?.context &&` to `data &&`
   * (render whenever data exists, regardless of context) — this specific test
   * would still pass, but the companion negative test below (context: null)
   * would then incorrectly render the label and fail its `queryByTestId`
   * assertion, proving the guard is load-bearing.
   */
  it('renders the context label when data.context is set', async () => {
    mockUseInternalNode.mockImplementation((id: string) =>
      id === 'a' ? mockMeasuredNode(0, 0) : mockMeasuredNode(200, 0),
    );
    const { FloatingEdge } = await import('./FloatingEdge');

    render(
      <FloatingEdge
        id="1-2"
        source="a"
        target="b"
        markerEnd="url(#arrow-end)"
        data={{ linkType: 'Unidirectional', context: 'blocks' }}
        {...basePositionProps}
      />,
    );

    expect(screen.getByTestId('edge-label-renderer')).toHaveTextContent('blocks');
  });

  /**
   * NEGATIVE PROOF: when `data.context` is null (linkType set but no context
   * text), no label wrapper renders at all.
   *
   * Mental-deletion: remove the `data?.context &&` guard entirely (always
   * render EdgeLabelRenderer) → `queryByTestId('edge-label-renderer')` would
   * resolve rather than being null, failing this assertion.
   */
  it('renders no label when data.context is null', async () => {
    mockUseInternalNode.mockImplementation((id: string) =>
      id === 'a' ? mockMeasuredNode(0, 0) : mockMeasuredNode(200, 0),
    );
    const { FloatingEdge } = await import('./FloatingEdge');

    render(
      <FloatingEdge
        id="1-2"
        source="a"
        target="b"
        markerEnd="url(#arrow-end)"
        data={{ linkType: 'Unidirectional', context: null }}
        {...basePositionProps}
      />,
    );

    expect(screen.queryByTestId('edge-label-renderer')).toBeNull();
  });

  /**
   * NEGATIVE PROOF (unchanged behaviour, regression guard): an unmeasured
   * endpoint still yields no render at all — the pre-existing early-return
   * guard is untouched by this PR's changes.
   */
  it('renders nothing when an endpoint is unmeasured', async () => {
    mockUseInternalNode.mockReturnValue(undefined);
    const { FloatingEdge } = await import('./FloatingEdge');

    const { container } = render(
      <FloatingEdge
        id="1-2"
        source="a"
        target="b"
        {...basePositionProps}
      />,
    );

    expect(container).toBeEmptyDOMElement();
  });
});
