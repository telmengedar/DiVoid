/**
 * WorkspaceCanvas — the @xyflow/react graph canvas for the workspace view.
 *
 * Responsibilities (this component only):
 *  - Own the ReactFlow instance and viewport state (local pan/zoom).
 *  - Compute padded bounds from the current viewport and pass to hooks.
 *  - Apply type and status filters via useNodesInViewport / useUntypedNodesInViewport.
 *  - Merge typed + untyped node sets when "untyped" is selected.
 *  - Convert NodeDetails[] + NodeLink[] → xyflow Node[] / Edge[].
 *  - Dispatch useMoveNode on drag-end.
 *  - Dispatch useLinkNodes on xyflow onConnect (drag-to-connect, #287).
 *  - Dispatch useUnlinkNodes on Delete key via onEdgesDelete, with undo toast (#266).
 *  - Handle file drop → create-node with inferred type + upload content.
 *  - Handle click-on-empty-space → open CreateNodeDialog with pre-filled position.
 *
 * Render-stability guardrails (DiVoid rule #271):
 *  - nodes / edges arrays are memoised over query results.
 *  - All xyflow event handlers are wrapped in useCallback.
 *  - nodeTypes / edgeTypes objects are declared outside the component (stable refs).
 *  - The bounds debounce prevents thrashing the query on every pan tick.
 *
 * Design: docs/architecture/workspace-mode.md §5.7
 * Task: DiVoid node #230 / #318 / #352
 */

import {
  useState,
  useCallback,
  useMemo,
  useEffect,
  useRef,
  DragEvent,
} from 'react';
import {
  ReactFlow,
  Background,
  Controls,
  MiniMap,
  useNodesState,
  useEdgesState,
  addEdge,
  ConnectionMode,
  type Edge,
  type Connection,
  type Viewport,
  type OnNodeDrag,
  type EdgeTypes,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { useTheme } from 'next-themes';
import { useNavigate } from 'react-router-dom';
import { toast } from 'sonner';

import {
  useNodesInViewport,
  useUntypedNodesInViewport,
  type ViewportBounds,
} from './useNodesInViewport';
import { useNodeAdjacency } from './useNodeAdjacency';
import { useMoveNode } from './useMoveNode';
import {
  useWorkspaceFilters,
  UNTYPED_VALUE,
  ALL_NODE_TYPES,
  ALL_STATUS_VALUES,
  NO_STATUS_VALUE,
} from './useWorkspaceFilters';
import {
  WorkspaceFilterPopover,
  type FilterOption,
} from './WorkspaceFilterPopover';
import { NodeCardRenderer, type NodeCardData, type WorkspaceNode } from './NodeCardRenderer';
import { FloatingEdge } from './FloatingEdge';
import { CreateNodeDialog } from '@/features/nodes/CreateNodeDialog';
import { useCreateNode, useLinkNodes, useUnlinkNodes } from '@/features/nodes/mutations';
import {
  NODE_DIMENSION_PADDING,
  VIEWPORT_DEBOUNCE_MS,
  DEFAULT_VIEWPORT_BOUNDS,
  DEFAULT_ZOOM,
  DEFAULT_PAN,
} from './constants';
import type { PositionedNodeDetails, NodeLink } from '@/types/divoid';
import { API } from '@/lib/constants';
import { useApiClient } from '@/lib/useApiClient';

// ─── Stable nodeTypes / edgeTypes references (must be outside component) ──────
// Declaring these inside the component would create a new object on every
// render, causing xyflow to unmount/remount every node or edge. This is a
// common render-loop pitfall documented in #271.
const nodeTypes = { nodeCard: NodeCardRenderer };
const edgeTypes: EdgeTypes = { floating: FloatingEdge };

// defaultEdgeOptions — typed once here so the object reference is stable.
const defaultEdgeOptions = { type: 'floating' } as const;

// ─── Stable MiniMap nodeColor callback (win #5) ───────────────────────────────
// Inline arrow → new function identity every render → MiniMap re-paints all
// nodes on every viewport change. Module-level constant breaks that cycle.
function miniMapNodeColor(n: { data?: unknown }): string {
  const type = (n.data as NodeCardData | undefined)?.type ?? '';
  switch (type) {
    case 'task':          return '#3b82f6';
    case 'bug':           return '#ef4444';
    case 'project':       return '#a855f7';
    case 'documentation': return '#f59e0b';
    case 'person':        return '#22c55e';
    case 'organization':  return '#14b8a6';
    default:              return '#6b7280';
  }
}

// ─── Session storage key for viewport persistence ─────────────────────────────
const VIEWPORT_SESSION_KEY = 'divoid.workspace.viewport';

// ─── Filter option labels ─────────────────────────────────────────────────────

/** Human-readable labels for synthetic / abbreviated type values. */
const TYPE_LABELS: Record<string, string> = {
  [UNTYPED_VALUE]: 'untyped',
};

/** Human-readable labels for synthetic / abbreviated status values. */
const STATUS_LABELS: Record<string, string> = {
  [NO_STATUS_VALUE]: 'no status',
};

const typeFilterOptions: FilterOption[] = ALL_NODE_TYPES.map((t) => ({
  value: t,
  label: TYPE_LABELS[t] ?? t,
}));

const statusFilterOptions: FilterOption[] = ALL_STATUS_VALUES.map((s) => ({
  value: s,
  label: STATUS_LABELS[s] ?? s,
}));

// ─── Helpers ─────────────────────────────────────────────────────────────────

/** Derive padded bounds from an xyflow viewport + container dimensions. */
function computePaddedBounds(
  viewport: Viewport,
  containerWidth: number,
  containerHeight: number,
): ViewportBounds {
  const { x: panX, y: panY, zoom } = viewport;

  // Visible world rect (top-left and bottom-right in world coordinates)
  const worldLeft   = -panX / zoom;
  const worldTop    = -panY / zoom;
  const worldRight  = (containerWidth - panX) / zoom;
  const worldBottom = (containerHeight - panY) / zoom;

  const pad = NODE_DIMENSION_PADDING / zoom;

  return [
    worldLeft  - pad,
    worldTop   - pad,
    worldRight + pad,
    worldBottom + pad,
  ];
}

/** Convert a PositionedNodeDetails into an xyflow WorkspaceNode. */
function toXyflowNode(n: PositionedNodeDetails): WorkspaceNode {
  return {
    id:       String(n.id),
    type:     'nodeCard',
    position: { x: n.x, y: n.y },
    data:     n as NodeCardData,
  };
}

/** Convert a NodeLink into an xyflow Edge. */
function toXyflowEdge(link: NodeLink, visibleIds: Set<string>): Edge | null {
  const src = String(link.sourceId);
  const tgt = String(link.targetId);
  // Only render edges where both endpoints are in the current visible set.
  if (!visibleIds.has(src) || !visibleIds.has(tgt)) return null;
  return {
    id:     `${src}-${tgt}`,
    source: src,
    target: tgt,
    type:   'floating',
  };
}

/** Infer a DiVoid node type from a dropped file's MIME type. */
function inferNodeType(mimeType: string): string {
  if (mimeType.startsWith('image/')) return 'image';
  if (mimeType.startsWith('text/markdown') || mimeType === 'text/plain') return 'documentation';
  if (mimeType === 'application/pdf') return 'documentation';
  return 'documentation';
}

/** Load initial viewport from sessionStorage or return defaults. */
function loadSavedViewport(): Viewport {
  try {
    const raw = sessionStorage.getItem(VIEWPORT_SESSION_KEY);
    if (raw) return JSON.parse(raw) as Viewport;
  } catch {
    // sessionStorage unavailable or corrupt — fall back.
  }
  return { x: DEFAULT_PAN.x, y: DEFAULT_PAN.y, zoom: DEFAULT_ZOOM };
}

/** Persist viewport to sessionStorage (best-effort). */
function saveViewport(vp: Viewport): void {
  try {
    sessionStorage.setItem(VIEWPORT_SESSION_KEY, JSON.stringify(vp));
  } catch {
    // Best-effort — ignore write errors (private browsing, quota).
  }
}

// ─── Component ────────────────────────────────────────────────────────────────

export function WorkspaceCanvas() {
  const { resolvedTheme } = useTheme();
  const navigate          = useNavigate();
  const client            = useApiClient();
  const moveNode          = useMoveNode();
  const createNode        = useCreateNode();
  const linkNodes         = useLinkNodes();
  const unlinkNodes       = useUnlinkNodes();

  // ── Filters ───────────────────────────────────────────────────────────────
  const {
    selectedTypes,
    selectedStatuses,
    toggleType,
    toggleStatus,
    typeFilterActive,
    statusFilterActive,
  } = useWorkspaceFilters();

  // Build the filter param object for useNodesInViewport.
  // Memoised so it doesn't cause query key churn on every render.
  const filterParams = useMemo(
    () => ({ selectedTypes, selectedStatuses }),
    [selectedTypes, selectedStatuses],
  );

  // Derive whether real types are selected (to decide if typed query runs).
  const hasRealTypes = useMemo(
    () => selectedTypes.some((t) => t !== UNTYPED_VALUE),
    [selectedTypes],
  );

  const selectedTypesSet    = useMemo(() => new Set(selectedTypes),    [selectedTypes]);
  const selectedStatusesSet = useMemo(() => new Set(selectedStatuses), [selectedStatuses]);

  // ── Container ref (to read pixel dimensions for bounds calc) ──────────────
  const containerRef = useRef<HTMLDivElement>(null);

  // ── Viewport ref (win #3: was useState — re-rendered canvas at ~60Hz on pan) ─
  // Only handlePaneClick and handleCanvasDrop read viewport for screen→world
  // conversion; both read from the ref at call time so no re-render is needed.
  const viewportRef = useRef<Viewport>(loadSavedViewport());

  // ── Debounced bounds for the viewport query ───────────────────────────────
  const [debouncedBounds, setDebouncedBounds] = useState<ViewportBounds | null>(null);
  const debounceTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Initialise bounds on first mount (after container is sized).
  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;
    setDebouncedBounds(
      computePaddedBounds(viewportRef.current, container.offsetWidth, container.offsetHeight),
    );
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []); // intentional: run once on mount

  // ── Data queries ──────────────────────────────────────────────────────────
  const { data: nodesPage } = useNodesInViewport(
    debouncedBounds ?? DEFAULT_VIEWPORT_BOUNDS,
    filterParams,
    hasRealTypes,
  );

  // Untyped fetch (option b from #318): only when UNTYPED_VALUE is selected.
  const includesUntyped = selectedTypes.includes(UNTYPED_VALUE);
  const { data: untypedPage } = useUntypedNodesInViewport(
    debouncedBounds ?? DEFAULT_VIEWPORT_BOUNDS,
    filterParams,
  );

  // Merge typed + untyped results. Deduplicate by id (a node cannot be both
  // typed and untyped, but guard anyway).
  const visibleDetails = useMemo<PositionedNodeDetails[]>(() => {
    const typed   = nodesPage?.result ?? [];
    const untyped = includesUntyped ? (untypedPage?.result ?? []) : [];

    if (untyped.length === 0) return typed;

    const seen = new Set(typed.map((n) => n.id));
    const merged = [...typed];
    for (const n of untyped) {
      if (!seen.has(n.id)) {
        merged.push(n);
        seen.add(n.id);
      }
    }
    return merged;
  }, [nodesPage, untypedPage, includesUntyped]);

  const visibleNodeIds = useMemo(
    () => visibleDetails.map((n) => n.id),
    [visibleDetails],
  );

  const { data: linksPage } = useNodeAdjacency(visibleNodeIds);

  // ── Build xyflow nodes / edges (memoised) ─────────────────────────────────
  const xyNodes = useMemo(
    () => visibleDetails.map(toXyflowNode),
    [visibleDetails],
  );

  const visibleIdSet = useMemo(
    () => new Set(visibleDetails.map((n) => String(n.id))),
    [visibleDetails],
  );

  const xyEdges = useMemo(
    () =>
      (linksPage?.result ?? [])
        .map((link) => toXyflowEdge(link, visibleIdSet))
        .filter((e): e is Edge => e !== null),
    [linksPage, visibleIdSet],
  );

  // ── Controlled xyflow state ───────────────────────────────────────────────
  // We use controlled state so xyflow's local drag deltas apply immediately
  // while the PATCH fires in the background.
  const [nodes, setNodes, onNodesChange] = useNodesState<WorkspaceNode>(xyNodes);
  const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>(xyEdges);

  // Sync server data into xyflow state whenever query results change.
  // Only replace positions for nodes NOT currently being dragged to avoid
  // snapping under the cursor.
  //
  // win #6: prev.find inside xyNodes.map was O(n²). Build prevMap once (O(n))
  // so each lookup is O(1) — measurable at the 250-node cap.
  useEffect(() => {
    setNodes((prev) => {
      const dragging = new Set(prev.filter((n) => n.dragging).map((n) => n.id));
      // Build Map<id, prev> once — O(n) instead of O(n) per map iteration.
      const prevMap  = new Map(prev.map((p) => [p.id, p]));
      const incoming = new Map(xyNodes.map((n) => [n.id, n]));

      // Remove nodes that left the viewport; add new ones; update non-dragging ones.
      return xyNodes.map((incomingNode) => {
        const existing = prevMap.get(incomingNode.id);
        if (existing && dragging.has(existing.id)) {
          // Preserve local drag position; update data (name/status) only.
          return { ...existing, data: incomingNode.data };
        }
        return incomingNode;
      }).concat(
        // Keep dragging nodes that scrolled out of viewport bounds temporarily.
        prev.filter((p) => dragging.has(p.id) && !incoming.has(p.id)),
      );
    });
  }, [xyNodes, setNodes]);

  useEffect(() => {
    setEdges(xyEdges);
  }, [xyEdges, setEdges]);

  // ── Drag-end handler ──────────────────────────────────────────────────────
  const handleNodeDragStop = useCallback<OnNodeDrag<WorkspaceNode>>(
    (_event, node) => {
      moveNode.mutate({
        id: Number(node.id),
        x: node.position.x,
        y: node.position.y,
      });
    },
    [moveNode],
  );

  // ── Viewport change ───────────────────────────────────────────────────────
  // win #3: viewportRef replaces useState — no re-render on pan/zoom.
  // win #2: saveViewport is folded into the existing debounce timer so the
  //         sessionStorage write fires at most once per VIEWPORT_DEBOUNCE_MS
  //         (~250ms) instead of every xyflow tick (~60Hz).
  const handleViewportChange = useCallback(
    (newViewport: Viewport) => {
      viewportRef.current = newViewport;
      if (debounceTimer.current) clearTimeout(debounceTimer.current);
      debounceTimer.current = setTimeout(() => {
        const container = containerRef.current;
        if (!container) return;
        setDebouncedBounds(
          computePaddedBounds(
            newViewport,
            container.offsetWidth,
            container.offsetHeight,
          ),
        );
        saveViewport(newViewport);
      }, VIEWPORT_DEBOUNCE_MS);
    },
    [],
  );

  // ── CreateNodeDialog state ─────────────────────────────────────────────────
  const [createDialogOpen, setCreateDialogOpen] = useState(false);
  const [createPosition, setCreatePosition]     = useState<{ x: number; y: number } | null>(null);

  // ── Click on empty canvas space → open CreateNodeDialog ───────────────────
  // win #3: reads viewportRef.current instead of viewport state — the handler
  // identity is now stable across pan/zoom (no viewport in deps array).
  const handlePaneClick = useCallback(
    (event: React.MouseEvent) => {
      // Convert screen coordinates to canvas world coordinates.
      const container = containerRef.current;
      if (!container) return;

      const vp        = viewportRef.current;
      const rect      = container.getBoundingClientRect();
      const screenX   = event.clientX - rect.left;
      const screenY   = event.clientY - rect.top;
      const worldX    = (screenX - vp.x) / vp.zoom;
      const worldY    = (screenY - vp.y) / vp.zoom;

      setCreatePosition({ x: worldX, y: worldY });
      setCreateDialogOpen(true);
    },
    [],
  );

  // ── File drop onto canvas blank space ─────────────────────────────────────
  const [isDragOver, setIsDragOver] = useState(false);

  const handleCanvasDragOver = useCallback((e: DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    setIsDragOver(true);
  }, []);

  const handleCanvasDragLeave = useCallback((e: DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    setIsDragOver(false);
  }, []);

  const handleCanvasDrop = useCallback(
    async (e: DragEvent<HTMLDivElement>) => {
      e.preventDefault();
      setIsDragOver(false);

      const file = e.dataTransfer.files?.[0];
      if (!file) return;

      // Compute drop world position.
      const container = containerRef.current;
      if (!container) return;
      const vp     = viewportRef.current;
      const rect   = container.getBoundingClientRect();
      const screenX = e.clientX - rect.left;
      const screenY = e.clientY - rect.top;
      const worldX  = (screenX - vp.x) / vp.zoom;
      const worldY  = (screenY - vp.y) / vp.zoom;

      // Create the node first, then upload content.
      try {
        const nodeType  = inferNodeType(file.type);
        const nodeBody  = { type: nodeType, name: file.name, x: worldX, y: worldY };
        const created   = await createNode.mutateAsync(nodeBody as Parameters<typeof createNode.mutateAsync>[0]);

        // Upload file content.
        await client.postRaw(
          API.NODES.CONTENT(created.id),
          file,
          file.type || 'application/octet-stream',
        );

        toast.success(`Created node "${file.name}"`);
      } catch {
        // Error toast already shown by mutation's onError.
      }
    },
    [createNode, client],
  );

  // ── After create-node from dialog ─────────────────────────────────────────
  const handleNodeCreated = useCallback(
    (id: number) => {
      setCreateDialogOpen(false);
      navigate(`/nodes/${id}`);
    },
    [navigate],
  );

  // ── Drag-to-connect (#287) ────────────────────────────────────────────────
  // xyflow fires onConnect when the user drags from a source handle and drops
  // on a target handle. We parse the string ids back to numbers, guard against
  // self-links, then call useLinkNodes. The optimistic edge is added to local
  // state immediately (addEdge) so the UI responds before the PATCH resolves;
  // the adjacency cache invalidation in useLinkNodes' onSuccess reconciles it.
  const handleConnect = useCallback(
    (connection: Connection) => {
      const sourceId = Number(connection.source);
      const targetId = Number(connection.target);

      // Guard: self-links are nonsensical in DiVoid.
      if (connection.source === connection.target) {
        toast.warning('Cannot link a node to itself');
        return;
      }

      // Optimistic local edge — replaced by the server-reconciled set on refetch.
      setEdges((eds) => addEdge(connection, eds));

      linkNodes.mutate({ sourceId, targetId });
    },
    [setEdges, linkNodes],
  );

  // ── Delete edge with undo (#266) ──────────────────────────────────────────
  // xyflow fires onEdgesDelete (via deleteKeyCode='Delete') with the array of
  // edges the user pressed Delete on. We dispatch useUnlinkNodes for each and
  // surface a sonner toast with an Undo action that re-links within ~5 s.
  const handleEdgesDelete = useCallback(
    (deletedEdges: Edge[]) => {
      for (const edge of deletedEdges) {
        const sourceId = Number(edge.source);
        const targetId = Number(edge.target);

        unlinkNodes.mutate(
          { sourceId, targetId },
          {
            onSuccess: () => {
              toast('Link removed', {
                duration: 5000,
                action: {
                  label: 'Undo',
                  onClick: () => {
                    linkNodes.mutate(
                      { sourceId, targetId },
                      {
                        onError: () => {
                          toast.error('Could not restore link. Please try again.');
                        },
                      },
                    );
                  },
                },
              });
            },
          },
        );
      }
    },
    [unlinkNodes, linkNodes],
  );

  // ── Render ────────────────────────────────────────────────────────────────
  return (
    <div
      ref={containerRef}
      className="relative h-full w-full"
      onDragOver={handleCanvasDragOver}
      onDragLeave={handleCanvasDragLeave}
      onDrop={handleCanvasDrop}
    >
      {/* Drop overlay */}
      {isDragOver && (
        <div
          className="pointer-events-none absolute inset-0 z-10 flex items-center justify-center rounded border-2 border-dashed border-primary bg-primary/10"
          aria-live="polite"
          aria-label="Drop file to create node"
        >
          <p className="text-primary font-medium text-sm">Drop file to create node</p>
        </div>
      )}

      {/* Toolbar overlay — filter controls */}
      <div
        className="pointer-events-none absolute top-3 left-3 z-20 flex items-center gap-2"
        aria-label="Workspace filter toolbar"
      >
        <div className="pointer-events-auto flex items-center gap-2">
          <WorkspaceFilterPopover
            label="Type"
            options={typeFilterOptions}
            selected={selectedTypesSet}
            onToggle={toggleType}
            active={typeFilterActive}
          />
          <WorkspaceFilterPopover
            label="Status"
            options={statusFilterOptions}
            selected={selectedStatusesSet}
            onToggle={toggleStatus}
            active={statusFilterActive}
          />
        </div>
      </div>

      <ReactFlow
        nodes={nodes}
        edges={edges}
        nodeTypes={nodeTypes}
        edgeTypes={edgeTypes}
        defaultEdgeOptions={defaultEdgeOptions}
        onNodesChange={onNodesChange}
        onEdgesChange={onEdgesChange}
        onConnect={handleConnect}
        onEdgesDelete={handleEdgesDelete}
        onNodeDragStop={handleNodeDragStop}
        onViewportChange={handleViewportChange}
        onPaneClick={handlePaneClick}
        defaultViewport={viewportRef.current}
        colorMode={resolvedTheme === 'dark' ? 'dark' : 'light'}
        fitView={false}
        minZoom={0.1}
        maxZoom={4}
        nodesConnectable={true}
        connectionMode={ConnectionMode.Loose}
        deleteKeyCode="Delete"
        onlyRenderVisibleElements
      >
        <Background />
        <Controls />
        <MiniMap
          nodeColor={miniMapNodeColor}
          pannable
          zoomable
        />
      </ReactFlow>

      {/* Create node dialog (pre-filled with click position) */}
      <CreateNodeDialog
        open={createDialogOpen}
        onOpenChange={setCreateDialogOpen}
        onCreated={handleNodeCreated}
        initialPosition={createPosition ?? undefined}
      />
    </div>
  );
}
