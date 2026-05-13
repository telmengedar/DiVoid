/**
 * WorkspaceCanvas — the @xyflow/react graph canvas for the workspace view.
 *
 * Responsibilities (this component only):
 *  - Own the ReactFlow instance and viewport state (local pan/zoom).
 *  - Compute padded bounds from the current viewport and pass to hooks.
 *  - Convert NodeDetails[] + NodeLink[] → xyflow Node[] / Edge[].
 *  - Dispatch useMoveNode on drag-end.
 *  - Handle file drop → create-node with inferred type + upload content.
 *  - Handle click-on-empty-space → open CreateNodeDialog with pre-filled position.
 *
 * Render-stability guardrails (DiVoid rule #271):
 *  - nodes / edges arrays are memoised over query results.
 *  - All xyflow event handlers are wrapped in useCallback.
 *  - nodeTypes object is declared outside the component (stable reference).
 *  - The bounds debounce prevents thrashing the query on every pan tick.
 *
 * Design: docs/architecture/workspace-mode.md §5.7
 * Task: DiVoid node #230
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
  type Node,
  type Edge,
  type NodeChange,
  type EdgeChange,
  type Connection,
  type Viewport,
  type OnNodeDrag,
  type NodeMouseHandler,
  type OnConnectEnd,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { useTheme } from 'next-themes';
import { useNavigate } from 'react-router-dom';
import { toast } from 'sonner';

import { useNodesInViewport, type ViewportBounds } from './useNodesInViewport';
import { useNodeAdjacency } from './useNodeAdjacency';
import { useMoveNode } from './useMoveNode';
import { NodeCardRenderer, type NodeCardData } from './NodeCardRenderer';
import { CreateNodeDialog } from '@/features/nodes/CreateNodeDialog';
import { useCreateNode } from '@/features/nodes/mutations';
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

// ─── Stable nodeTypes reference (must be outside component) ──────────────────
// Declaring nodeTypes inside the component would create a new object on every
// render, causing xyflow to unmount/remount every node. This is a common
// render-loop pitfall documented in #271.
const nodeTypes = { nodeCard: NodeCardRenderer };

// ─── Session storage key for viewport persistence ─────────────────────────────
const VIEWPORT_SESSION_KEY = 'divoid.workspace.viewport';

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

/** Convert a PositionedNodeDetails into an xyflow Node<NodeCardData>. */
function toXyflowNode(n: PositionedNodeDetails): Node<NodeCardData> {
  return {
    id:       String(n.id),
    type:     'nodeCard',
    position: { x: n.x, y: n.y },
    data:     n,
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
    type:   'default',
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

  // ── Container ref (to read pixel dimensions for bounds calc) ──────────────
  const containerRef = useRef<HTMLDivElement>(null);

  // ── Viewport state ────────────────────────────────────────────────────────
  const [viewport, setViewport] = useState<Viewport>(loadSavedViewport);

  // ── Debounced bounds for the viewport query ───────────────────────────────
  const [debouncedBounds, setDebouncedBounds] = useState<ViewportBounds | null>(null);
  const debounceTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const scheduleBoundsUpdate = useCallback((newViewport: Viewport) => {
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
    }, VIEWPORT_DEBOUNCE_MS);
  }, []);

  // Initialise bounds on first mount (after container is sized).
  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;
    setDebouncedBounds(
      computePaddedBounds(viewport, container.offsetWidth, container.offsetHeight),
    );
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []); // intentional: run once on mount

  // ── Data queries ──────────────────────────────────────────────────────────
  const { data: nodesPage } = useNodesInViewport(
    debouncedBounds ?? DEFAULT_VIEWPORT_BOUNDS,
  );

  const visibleDetails = useMemo(
    () => nodesPage?.result ?? [],
    [nodesPage],
  );

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
  const [nodes, setNodes, onNodesChange] = useNodesState<Node<NodeCardData>>(xyNodes);
  const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>(xyEdges);

  // Sync server data into xyflow state whenever query results change.
  // Only replace positions for nodes NOT currently being dragged to avoid
  // snapping under the cursor.
  useEffect(() => {
    setNodes((prev) => {
      const dragging = new Set(prev.filter((n) => n.dragging).map((n) => n.id));
      const incoming = new Map(xyNodes.map((n) => [n.id, n]));

      // Remove nodes that left the viewport; add new ones; update non-dragging ones.
      return xyNodes.map((incoming_node) => {
        const existing = prev.find((p) => p.id === incoming_node.id);
        if (existing && dragging.has(existing.id)) {
          // Preserve local drag position; update data (name/status) only.
          return { ...existing, data: incoming_node.data };
        }
        return incoming_node;
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
  const handleNodeDragStop = useCallback<OnNodeDrag<Node<NodeCardData>>>(
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
  const handleViewportChange = useCallback(
    (newViewport: Viewport) => {
      setViewport(newViewport);
      saveViewport(newViewport);
      scheduleBoundsUpdate(newViewport);
    },
    [scheduleBoundsUpdate],
  );

  // ── CreateNodeDialog state ─────────────────────────────────────────────────
  const [createDialogOpen, setCreateDialogOpen] = useState(false);
  const [createPosition, setCreatePosition]     = useState<{ x: number; y: number } | null>(null);

  // ── Click on empty canvas space → open CreateNodeDialog ───────────────────
  const handlePaneClick = useCallback(
    (event: React.MouseEvent) => {
      // Convert screen coordinates to canvas world coordinates.
      const container = containerRef.current;
      if (!container) return;

      const rect      = container.getBoundingClientRect();
      const screenX   = event.clientX - rect.left;
      const screenY   = event.clientY - rect.top;
      const worldX    = (screenX - viewport.x) / viewport.zoom;
      const worldY    = (screenY - viewport.y) / viewport.zoom;

      setCreatePosition({ x: worldX, y: worldY });
      setCreateDialogOpen(true);
    },
    [viewport],
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
      const rect   = container.getBoundingClientRect();
      const screenX = e.clientX - rect.left;
      const screenY = e.clientY - rect.top;
      const worldX  = (screenX - viewport.x) / viewport.zoom;
      const worldY  = (screenY - viewport.y) / viewport.zoom;

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
    [viewport, createNode, client],
  );

  // ── After create-node from dialog ─────────────────────────────────────────
  const handleNodeCreated = useCallback(
    (id: number) => {
      setCreateDialogOpen(false);
      navigate(`/nodes/${id}`);
    },
    [navigate],
  );

  // ── Edge connection (no-op in this PR — link-by-drag is #287) ─────────────
  const handleConnect = useCallback(
    (connection: Connection) => {
      setEdges((eds) => addEdge(connection, eds));
    },
    [setEdges],
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

      <ReactFlow
        nodes={nodes}
        edges={edges}
        nodeTypes={nodeTypes}
        onNodesChange={onNodesChange}
        onEdgesChange={onEdgesChange}
        onConnect={handleConnect}
        onNodeDragStop={handleNodeDragStop}
        onViewportChange={handleViewportChange}
        onPaneClick={handlePaneClick}
        defaultViewport={viewport}
        colorMode={resolvedTheme === 'dark' ? 'dark' : 'light'}
        fitView={false}
        minZoom={0.1}
        maxZoom={4}
        nodesConnectable={false}
        deleteKeyCode={null}
      >
        <Background />
        <Controls />
        <MiniMap
          nodeColor={(n) => {
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
          }}
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
