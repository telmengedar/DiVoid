# Architectural Document: In-canvas Modal Preview on Workspace Node Click

**Author:** Sarah (software architect)
**Date:** 2026-05-27
**Status:** Proposed — implementation by Pierre in a follow-up PR
**Task:** DiVoid node **#1253**
**Code contracts:** Frontend contracts node **#420**; Design contracts node **#1136**
**Lifts deferral from:** DiVoid node **#299** (PR 4b click-a-node Option B was rejected; the deferral is now being lifted)
**Distinct from:** DiVoid node **#290** (in-card content peek — different shape, still deferred)
**Render-stability rule:** DiVoid node **#271** (binding constraint)
**Cache-key foundation:** PR #124 (`['nodes', 'viewport']` invalidation prefix)
**Companion architecture:** DiVoid node **#283** (workspace mode design — must not regress)

---

## 0. User words

Verbatim, 2026-05-27 (after PR #124 merge):

> "okay, merged, deployed - seems to be working. Now i still want the qol feature of presenting task details as modal window directly in workspace instead of switching to another view. Makes exploring the workspace way easier."

The spec is: **a modal window**, opened by **clicking a node card on the workspace**, showing **the full detail view**, **without leaving the workspace route**. Goal: **easier exploration**. Everything in this document tracks that intent and nothing else.

---

## 1. Problem Statement

When a user explores the workspace graph today, clicking a node card calls `navigate(ROUTES.NODE_DETAIL(data.id))` in `NodeCardRenderer.tsx`. This unmounts the canvas, navigates to `/nodes/{id}`, and forces the user to use the back button to return — at which point the workspace re-mounts and re-runs its viewport query. Exploration is friction-heavy: every "what does this node say?" question costs a full route round-trip.

The user wants a **modal** overlay that surfaces the same content (read AND write affordances) without leaving the workspace route. Per #299 the option was rejected for PR 4 only because the canvas's render-stability was fragile at the time; #271 saga had just concluded. With PR #124 landed (and the canvas stable for weeks), the deferral is lifted.

Success criteria:

1. Clicking a node card on `/workspace` opens an in-canvas modal showing the node's full detail view.
2. Closing the modal (ESC, click-outside, close button, browser back) returns the user to the **exact** canvas they left — no viewport reset, no re-mount.
3. The modal supports the same write affordances as `/nodes/{id}` (Edit, Delete, Link, Unlink, Upload).
4. Mutations made inside the modal are reflected on the underlying canvas (renamed node card, deleted node disappears, new edge appears) without a route navigation.
5. The modal state is URL-reflected so it survives browser back/forward and is shareable as a link.
6. The existing `/nodes/{id}` route is unchanged. Direct visits and non-workspace links still navigate.
7. The canvas's render-stability (#271 / PRs #40–#42) is preserved.

---

## 2. Scope & Non-Scope

### In scope

- Extract `NodeDetailPage` body into a reusable `NodeDetailView` component consumed by **both** the existing route and the new modal.
- Replace the `navigate(...)` call in `NodeCardRenderer.handleClick` with peek-state mutation.
- Host the modal at the `WorkspacePage` level (above `WorkspaceCanvas`) so the canvas's render path never sees modal state.
- URL-state strategy via a single `?peek=<id>` query parameter, read by `WorkspacePage`, written by the canvas-click handler.
- Deep-link entry: `/workspace?peek=42` opens with the modal pre-opened.
- Mutation propagation that piggy-backs on the existing `['nodes', 'list']`, `['nodes', 'linkedto']`, `['nodes', 'viewport']` cache-key prefixes — no new invalidation scheme.

### Explicitly out of scope (this design does not specify any of these)

1. **In-card content peek on the canvas itself** — that is DiVoid #290, different shape, still deferred.
2. **Side-drawer / sheet alternative** — the user asked for a modal; #299 already explored the drawer option and rejected it.
3. **Multi-modal stacking** — clicking a neighbour row inside the modal does NOT push a new modal onto a stack. It **replaces** the current peek (same modal, different node), see §6.3. No "back through nested peeks" navigation.
4. **Type-specific modal layouts** — the modal renders the same regions regardless of node type, exactly like `/nodes/{id}` does today.
5. **NodeDetailPage internals refactor** — the four regions (metadata / content / neighbours / dialogs) are moved as-is into `NodeDetailView`. No prop renames, no behavioural changes beyond container shape.
6. **A "share peek" affordance** — sharing is implicit via the URL; no dedicated share button.
7. **Test breakdown for Pierre** — Pierre selects the test surface that pins the contract in §10; this design specifies the behavioural contract, not the unit test inventory.
8. **Workspace toolbar additions** — no new buttons, toggles, or popovers.

This list is the §4-of-brief anti-complexity gate. None of the seven items has a user request behind it.

---

## 3. Assumptions & Constraints

- **Stack assumed available:** `@radix-ui/react-dialog` (already used by `EditNodeDialog`, `DeleteNodeDialog`, `LinkNodeDialog`, `CreateNodeDialog`); `react-router-dom@7` with `useSearchParams`; TanStack Query v5; `@xyflow/react` v12. No new dependencies.
- **Radix Dialog nesting is supported** — each Radix `Dialog.Root` mounts via its own Portal to `document.body`, with its own overlay/content stacking via `z-index`. The existing Edit / Delete / Link dialogs nest inside the new peek modal by virtue of being Radix-portaled siblings, not DOM children of the modal content. Z-order is established by mount order; the inner dialog is mounted later and naturally lands on top.
- **The modal does NOT live inside `WorkspaceCanvas`** — see §5 and §9. It lives in `WorkspacePage`. This is load-bearing for §9.
- **`['nodes', 'viewport']` is the canonical invalidation prefix** — established by PR #124 and used by `useLinkNodes`, `useUnlinkNodes` in `mutations.ts`. `usePatchNode` and `useDeleteNode` already invalidate `['nodes', 'list']` and `['nodes', 'linkedto']`. The canvas's `useNodesInViewport` query uses a `['nodes', 'viewport', ...]` key, so prefix-matching invalidation reaches it. The design relies on this; no new invalidation paths are required.
- **No backend changes.** Pure frontend design. The DiVoid backend serves `/api/nodes/{id}` and content/links the same way the existing route consumes them.
- **xyflow's `deleteKeyCode="Delete"` does not collide with ESC** — Radix Dialog handles ESC internally with `event.stopPropagation()`, so xyflow's keyboard listener does not see the ESC that closes the modal. Verified by the pre-existing `CreateNodeDialog` already mounting inside the canvas DOM tree without ESC collisions.

---

## 4. Architectural Overview

```
                           ┌──────────────────────────────┐
                           │       WorkspacePage          │
                           │                              │
                           │  ┌────────────────────────┐  │
                           │  │  usePeekState (URL)    │  │
                           │  │  reads ?peek=<id>      │  │
                           │  │  writes via setParams  │  │
                           │  └─────────┬──────────────┘  │
                           │            │                 │
                           │  ┌─────────┴──────────────┐  │
                           │  │   WorkspaceCanvas      │  │
                           │  │                        │  │
                           │  │   NodeCardRenderer     │  │
                           │  │   ─ on click ────────► onPeek(id)
                           │  │                        │  │
                           │  └────────────────────────┘  │
                           │                              │
                           │  ┌────────────────────────┐  │
                           │  │  WorkspaceNodePeekModal│  │
                           │  │  (mounted iff peek≠0)  │  │
                           │  │  ┌─────────────────┐   │  │
                           │  │  │ NodeDetailView  │   │  │
                           │  │  │ (extracted from │   │  │
                           │  │  │ NodeDetailPage) │   │  │
                           │  │  └─────────────────┘   │  │
                           │  └────────────────────────┘  │
                           └──────────────────────────────┘

   /workspace?peek=42  ◄────────────────►  URL is source of truth
```

Three changes in the workspace tree:

1. **`WorkspacePage`** owns the peek state, derives it from `?peek=<id>`, hands a stable `onPeek(id: number) => void` callback to the canvas and a stable `onClose()` callback to the modal.
2. **`WorkspaceCanvas` + `NodeCardRenderer`** receive `onPeek` via a stable callback prop (or via context — see §5.3) instead of calling `useNavigate` directly. Render-stability discipline preserved.
3. **`WorkspaceNodePeekModal`** is a Radix `Dialog.Root` controlled by `peekId !== null`, with `NodeDetailView` as its body.

One change outside the workspace tree:

4. **`NodeDetailPage`** is reduced to a route shell: param parsing, invalid-id guard, render `<NodeDetailView nodeId={nodeId} variant="route" />`. Everything else moves into `NodeDetailView`.

---

## 5. Components & Responsibilities

### 5.1 `NodeDetailView` (NEW — `frontend/src/features/nodes/NodeDetailView.tsx`)

**Single responsibility:** render the four canonical regions of a node detail — metadata, content, linked neighbours, and the three write dialogs (Edit, Delete, Link) — as a self-contained, container-agnostic block.

**Props:**

| Prop | Type | Semantics |
|------|------|-----------|
| `nodeId` | `number` | The node to render. Must be `> 0`; caller is responsible for validating route params or peek ids. |
| `variant` | `'route' \| 'modal'` | Container hint. The view's content does not branch on this value; it is forwarded to the dialogs / neighbour navigation so they know whether to close-the-modal-on-success vs navigate-away-on-success (see §5.1.2). |
| `onClose` | `() => void` (optional) | Called when an action implies the view should close itself. Set by the modal container; the route container passes `undefined`, and the view treats the closing-implied action as "stay" (the existing `NodeDetailPage` behaviour). |
| `onNeighbourClick` | `(neighbourId: number) => void` (optional) | Called when the user clicks a neighbour row. Set by the modal container to swap the peek (see §6.3). Route container passes `undefined`, the neighbour row falls back to its current `<Link>` behaviour. |

**Does own:**
- Fetching `useNode(nodeId)`, `useNodeContent(...)`, `useNodeListLinkedTo(...)` (move verbatim from `NodeDetailPage`).
- Rendering metadata / content / neighbours regions exactly as today.
- Opening Edit / Delete / Link dialogs from local `useState` (`editOpen`, `deleteOpen`, `linkOpen`) — move verbatim.
- Permission gating via `useWhoami` — move verbatim.
- Surfacing query errors via `sonner` toasts — move verbatim.

**Does NOT own:**
- Route param parsing (lives in the route shell — `NodeDetailPage`).
- The back button (lives in the route shell; the modal uses the X / overlay-click / ESC instead).
- The container chrome (route uses `<div className="mx-auto max-w-5xl px-4 py-6">`; modal provides its own scroll container — see §5.3).

#### 5.1.1 Variant-conditional behaviour

The component is **container-agnostic for read flow** — the metadata + content + neighbours regions render identically in either variant. The only places `variant` is observed:

- **DeleteNodeDialog `onDeleted`** — in route variant, navigate to `/search` (today's behaviour). In modal variant, call `onClose()`. The canvas re-renders because `useDeleteNode` already invalidates `['nodes', 'list']` and `['nodes', 'linkedto']` and (per §6.1 of this design) gets a new invalidation for `['nodes', 'viewport']`.
- **Neighbour row click** — in route variant, `<Link to={ROUTES.NODE_DETAIL(n.id)}>` (today's behaviour). In modal variant, call `onNeighbourClick(n.id)`; the row becomes a button.

That is the entire conditional surface. Everything else is identical between containers.

#### 5.1.2 Why pass `variant` rather than separate Route/Modal wrappers

The two variants differ in exactly two callbacks (`onClose`, `onNeighbourClick`). Splitting into two sibling components (`NodeDetailRouteView`, `NodeDetailModalView`) per §5.4 of #420 (no-conditional-hooks) is unwarranted: there are no conditional hooks, no hook count difference, no shape divergence. A single component with optional callbacks is the simpler version per §1 of #1136. The §5.4 rule about splitting applies when the choice of *hook* depends on a prop value — not when two optional callbacks switch behaviour at event-handler time. Passing `variant` solely to forward to dialogs that need a render-side branch is acceptable, but if the dialogs can be told purely by callback presence (`onClose === undefined` ⇒ route, `onClose != null` ⇒ modal), `variant` can be dropped entirely. Pierre's call at implementation time; the contract is the same either way.

### 5.2 `NodeDetailPage` (REFACTORED — same path)

**After refactor — single responsibility:** parse `:id` from the URL, validate, render `NodeDetailView` with `variant="route"`. Plus retain the back button (which depends on `sessionStorage('divoid.lastLocation')` and is route-specific).

Pseudo-shape (prose, not code):
- `useParams` → `nodeId`.
- Invalid-id guard (existing behaviour).
- Render: `<BackButton />` then `<NodeDetailView nodeId={nodeId} variant="route" />` inside the existing `max-w-5xl` container.

The back button stays on the route only — modals don't have a back button; closing the modal IS the back.

### 5.3 `WorkspaceNodePeekModal` (NEW — `frontend/src/features/workspace/WorkspaceNodePeekModal.tsx`)

**Single responsibility:** wrap `NodeDetailView` in a Radix Dialog and translate Radix open/close events into peek-state changes.

**Props:**

| Prop | Type | Semantics |
|------|------|-----------|
| `peekId` | `number \| null` | Which node to peek. `null` means closed. |
| `onClose` | `() => void` | Called when Radix would close the dialog (overlay click, ESC, X button) or when an internal action implies close (Delete success). |
| `onPeekChange` | `(id: number) => void` | Called when the user clicks a neighbour row inside the modal — peek swap (see §6.3). |

**Does own:**
- The Radix `Dialog.Root` controlled by `open={peekId !== null}`.
- The overlay + content layout (scrollable, max-width, vertical centering — matches the existing dialog visual idiom).
- The X-close button and ARIA-modal correctness.
- Forwarding focus-trap and focus-return to Radix (default behaviour).

**Does NOT own:**
- Data fetching (delegated to `NodeDetailView`).
- URL state (delegated to `WorkspacePage`).
- The peek-swap logic itself (it forwards to `onPeekChange`).

**Render-conditional rule:** the inner Radix content is mounted only when `peekId !== null`. Specifically:

- `Dialog.Root open={peekId !== null}` controls visibility.
- Inside `Dialog.Content`, render `peekId !== null && <NodeDetailView nodeId={peekId} ... />` — i.e. only mount the view (and therefore its queries) when there is an id to peek. Radix unmounts `Dialog.Content` on close anyway, but this belt-and-braces guard ensures the view's hooks never see `nodeId={0}` even on close-transition frames.

### 5.4 `WorkspacePage` (MODIFIED)

**New responsibility added:** own the peek state and mount the modal as a sibling of the canvas.

**Peek state shape — derived state, not held state.** A custom hook `usePeekState()`:

- Reads `?peek=<id>` from `useSearchParams()` and parses to `number | null` (positive integer required; otherwise `null`).
- Returns `{ peekId: number | null, openPeek: (id: number) => void, closePeek: () => void }`.
- `openPeek(id)` calls `setSearchParams(prev => { prev.set('peek', String(id)); return prev; }, { replace: false })`. The non-replace push means browser back closes the modal.
- `closePeek()` calls `setSearchParams(prev => { prev.delete('peek'); return prev; }, { replace: false })`.

The two callbacks are wrapped in `useCallback` with stable deps (`setSearchParams` is stable per react-router-dom). The hook's return shape is referentially stable across renders that don't change `peekId` — so passing it down to `WorkspaceCanvas` and `WorkspaceNodePeekModal` does NOT cascade.

**Layout:**

```
WorkspacePage
└── WorkspaceErrorBoundary
    └── <div className="h-full w-full">
        ├── <WorkspaceCanvas onPeek={openPeek} />
        └── <WorkspaceNodePeekModal
              peekId={peekId}
              onClose={closePeek}
              onPeekChange={openPeek}
            />
```

The error boundary still wraps the canvas only. The modal is a sibling outside the boundary because Radix portals it to `document.body` anyway — a render error inside `NodeDetailView` cascades up to the Suspense boundary at the app root, not the canvas error boundary. (If Pierre measures that uncaught modal renders harm UX, a separate error boundary can wrap the modal in a follow-up; not in scope here.)

### 5.5 `WorkspaceCanvas` (MODIFIED — minimal surface)

**Change:** accept `onPeek: (id: number) => void` as a prop and forward it to `NodeCardRenderer`. Forwarding mechanism: see §5.6 — use the ReactFlow node `data` payload (`onPeek` is added per-node when building `xyNodes`).

`useNavigate()` stays in `WorkspaceCanvas` because it is still used by `handleNodeCreated` (which navigates after Create). That is unrelated to the peek flow.

**Render-stability rule (LOAD-BEARING):** `onPeek` is a stable callback (from `useCallback` in `WorkspacePage`'s `usePeekState`). Including it in the `xyNodes` data does NOT churn the memoised `xyNodes` array because `onPeek`'s reference is stable. The existing `propsAreEqual` in `NodeCardRenderer` does NOT compare `onPeek`; it stays as-is and continues to fire the latest closure. See §9 for the full lifecycle.

### 5.6 `NodeCardRenderer` (MODIFIED)

**Change:** instead of calling `useNavigate` and `navigate(ROUTES.NODE_DETAIL(data.id))`, call `data.onPeek(data.id)`.

The `onPeek` callback is plumbed into each `NodeCardData` payload by `toXyflowNode` in `WorkspaceCanvas`. `NodeCardData` gains one optional field of type `(id: number) => void`.

**Render-stability rule (LOAD-BEARING):**

- `propsAreEqual` is **not** extended to compare `data.onPeek`. The reference is stable across renders (per §5.4); even if Pierre's implementation drift caused churn here, `propsAreEqual` already returns `true` based on `id/name/type/status/selected` — the card would not re-render on `onPeek`-reference change. This is intentional. The latest closure fires because React closures inside a memoised component capture whatever `data.onPeek` is at the time of the click event (which dereferences `data` at handler-call time, not at render time).
- `useNavigate` is removed from `NodeCardRenderer`. One fewer hook per card.

`handleClick` becomes: read `data.onPeek` (optional chain for safety), call it with `data.id`. If `data.onPeek` is undefined for any reason (defensive), do nothing — the card silently fails. Pierre may instead choose to fall back to `useNavigate(ROUTES.NODE_DETAIL(data.id))` — both are acceptable; the design does not mandate a fallback because the orchestrator owns the `onPeek` prop and a missing one is a wiring bug, not a runtime condition.

---

## 6. Interactions & Data Flow

### 6.1 Open peek

1. User clicks node card on canvas.
2. `NodeCardRenderer.handleClick` calls `data.onPeek(data.id)`.
3. `onPeek` (which is `openPeek` from `usePeekState`) calls `setSearchParams(prev => prev.set('peek', String(id)))`.
4. `WorkspacePage` re-renders with `peekId = id`. The canvas does NOT re-render — `WorkspaceCanvas`'s own queries, viewport state, and xyflow state are unaffected.
5. `WorkspaceNodePeekModal` re-renders. Its `Dialog.Root` transitions from `open=false` to `open=true`. Radix mounts `Dialog.Content` via Portal. `NodeDetailView` mounts; its three queries (`useNode`, `useNodeContent`, `useNodeListLinkedTo`) fire.
6. The user sees skeleton loaders (existing behaviour from `NodeDetailPage`) until the queries resolve.

### 6.2 Close peek

Three triggers, single path:

1. **ESC, click on overlay, X button** — Radix calls `onOpenChange(false)`, the modal calls `onClose()`, which calls `closePeek()` from `usePeekState`, which deletes `peek` from the URL params.
2. **Browser back button** — react-router pops the history entry that contained `?peek=X` (because step 6.1 used `replace: false`). `WorkspacePage` re-derives `peekId = null` from the new URL. `Dialog.Root` transitions to `open=false`. Radix unmounts `Dialog.Content`. The view's queries are unmounted; TanStack Query's GC keeps their data warm for `staleTime`.
3. **Delete success inside modal** — `DeleteNodeDialog.onDeleted` fires `onClose()` (the modal variant's branch — see §5.1.1). Same path as ESC.

In all three cases the canvas is undisturbed.

### 6.3 Peek swap (neighbour-row click inside modal)

1. User clicks a neighbour row in the modal's neighbours region.
2. Row is a button (modal variant); on click it calls `onNeighbourClick(neighbour.id)` which is `openPeek` from `WorkspacePage` (§5.3 prop chain).
3. `openPeek` sets `?peek=<newId>`. `WorkspacePage` re-derives `peekId = newId`.
4. The modal does NOT unmount and re-mount. The Dialog stays open. `NodeDetailView`'s `nodeId` prop changes; its `useNode(nodeId)` query keys on the new id and fetches. The same Dialog frame transitions content.
5. Loading state is the skeleton already in `NodeDetailView`.

This is "collapse-and-reopen" in URL terms (one history entry replaces the previous peek's parameter), but visually it is a content swap inside a stable modal. **No nested-modal stack.** Browser back from a peek-swap returns to the URL with the **previous** `peek` value because `setSearchParams` defaults to push (non-replace). The user can step back through their peek trail via browser back. Pierre may at his discretion call `setSearchParams(..., { replace: true })` for neighbour clicks to avoid trail clutter — the contract this design fixes is "swap, not stack"; the history shape is implementation-tunable.

### 6.4 Deep link

1. User opens `/workspace?peek=42` directly (bookmark, shared link, browser refresh on an existing peek).
2. `WorkspacePage` mounts. `usePeekState` parses `peekId = 42`.
3. `WorkspaceCanvas` mounts and runs its viewport query as usual. The canvas content does not depend on `peekId`.
4. `WorkspaceNodePeekModal` mounts with `peekId=42`. The modal is open from frame 0.
5. If node 42 doesn't exist or the user has no permission, `useNode(42)` errors — `NodeDetailView` renders the existing 404 / error state inside the modal (existing behaviour copied from `NodeDetailPage`).

### 6.5 Mutation propagation

The TanStack Query cache is the single source of truth; the modal and the canvas observe the same cache.

| Action | Mutation hook | Existing invalidation | Canvas observes |
|--------|---------------|----------------------|-----------------|
| Edit name/status (modal Edit dialog) | `usePatchNode(id)` | `['nodes', id]`, `['nodes', 'list']`, `['nodes', 'linkedto']`, `['nodes', 'semantic']` | `useNodesInViewport`'s key starts with `['nodes', 'viewport', ...]` — NOT invalidated by `usePatchNode` today. **DESIGN-LEVEL ACTION:** add `queryClient.invalidateQueries({ queryKey: ['nodes', 'viewport'] })` to `usePatchNode.onSuccess`. Trivial one-line change in `mutations.ts`. |
| Delete (modal Delete dialog) | `useDeleteNode(id)` | `['nodes', id]` removed, content removed, `['nodes', 'list']`, `['nodes', 'linkedto']`, `['nodes', 'semantic']` | Same gap. **DESIGN-LEVEL ACTION:** add `['nodes', 'viewport']` invalidation to `useDeleteNode.onSuccess`. |
| Link (modal Link dialog) | `useLinkNodes()` | invalidates `['nodes', 'viewport']` per PR #124 | covered — canvas refetches and rebuilds edges via `buildEdgesFromInlineLinks` |
| Unlink (modal Unlink button) | `useUnlinkNodes()` | invalidates `['nodes', 'viewport']` per PR #124 | covered |
| Upload content | `useUploadContent(id)` | invalidates `['nodes', id]`, content key | canvas does not display content; no extra invalidation needed |

The two design-level mutation-hook adjustments above (`usePatchNode` and `useDeleteNode` gaining a `['nodes', 'viewport']` invalidation) are **the only mutation-layer changes**. They benefit the non-workspace `/nodes/{id}` route as well (a user editing a node on the route then visiting `/workspace` no longer sees a stale card). PR #124's invalidation precedent applies cleanly.

### 6.6 Closing the modal does not reset the canvas

Closing transitions only the modal subtree. The canvas's `WorkspaceCanvas` component instance is preserved across the open/close cycle. Its `useNodesInViewport` query stays mounted with whatever data it last had. The viewport position is held in `viewportRef.current` (which is a `useRef`, not a `useState`, per the existing `win #3` optimisation) — completely untouched by the peek lifecycle. ESC closes the modal and the canvas re-shows immediately with the user's exact pan/zoom intact.

---

## 7. Data Model (Conceptual)

No new entities. No new persisted state. No new query keys.

URL surface adds one parameter:

- `peek` (integer, optional, positive) on `/workspace`. Absence and any non-positive-integer value mean "no peek".

Component-local state additions:

- `WorkspacePage` derives `peekId: number | null` from the URL via `usePeekState`.
- `NodeDetailView` retains the three local `useState<boolean>` flags it inherits from `NodeDetailPage` for the inner Edit / Delete / Link dialogs. These are component-internal modal-open flags, not peek-state.

---

## 8. Contracts & Interfaces (Abstract)

### 8.1 `NodeDetailView` contract

- **Input:** `nodeId` (positive integer), `variant`, `onClose` (optional), `onNeighbourClick` (optional).
- **Output (rendered):** the four canonical regions: metadata, content, neighbours, plus the three nested dialogs. Identical visual shape between variants except for: (a) Delete success behaviour, (b) neighbour row anchor vs button.
- **Output (effects):** the three queries (`useNode`, `useNodeContent`, `useNodeListLinkedTo`) fire when `nodeId` changes; mutation hooks invalidate caches per §6.5.
- **Invariants:**
  - The view is pure-render given `(nodeId, variant)` and stable callbacks. Same inputs ⇒ same hooks fire in the same order ⇒ same query keys.
  - When `nodeId` changes from A to B, the view's hooks re-key to B; the user sees a skeleton then B's content. No stale flash because the hooks are keyed by `nodeId` (existing pattern).
  - Mounting the view does NOT mutate any global state. Closing it does NOT trigger any cleanup beyond TanStack Query's standard unmount path.

### 8.2 `WorkspaceNodePeekModal` contract

- **Input:** `peekId` (number or null), `onClose`, `onPeekChange`.
- **Output (rendered):** when `peekId !== null`, a Radix Dialog with `NodeDetailView` inside. When `peekId === null`, nothing visible (Radix Dialog is closed and the portal unmounts the content).
- **Output (effects):** calls `onClose` on overlay click / ESC / X. Calls `onPeekChange(id)` when neighbour row is clicked.
- **Invariants:**
  - The modal does NOT mount queries when `peekId === null` (§5.3 belt-and-braces).
  - Focus is trapped inside the modal while open. Focus returns to whatever was focused at open time on close (Radix default).
  - ESC handling is intercepted by Radix; xyflow's `deleteKeyCode` listener does not see ESC.

### 8.3 `usePeekState` hook contract

- **Return:** `{ peekId: number | null, openPeek: (id: number) => void, closePeek: () => void }`.
- **Invariants:**
  - `peekId` reflects the URL on every render — single source of truth.
  - `openPeek(id)` pushes a new history entry (non-replace) so browser back closes the peek.
  - `closePeek()` pushes a new history entry that removes the param. Browser back from a closed state thus goes back to the URL that had the peek open — round-trip-able.
  - Both callbacks are referentially stable across renders (`useCallback` with `[setSearchParams]` deps; `setSearchParams` is itself stable per react-router-dom 7).

### 8.4 `NodeCardData` (`PositionedNodeDetails & Record<string, unknown>`) gains:

| New field | Type | Semantics |
|-----------|------|-----------|
| `onPeek` | `(id: number) => void` | The peek-open callback. Plumbed by `WorkspaceCanvas` when building `xyNodes` from `visibleDetails`. Stable across canvas renders. |

This is a sibling addition; no other `NodeCardData` field changes shape. `propsAreEqual` in `NodeCardRenderer` does not gain a comparison for `onPeek` (§5.6).

---

## 9. Render-stability Demonstration (DiVoid #271 — binding constraint)

This section answers Audit Item 6.7 from the brief. Per #271, an issue affects "every place tomorrow" if a shared invariant escapes its container. The PRs #40–#42 saga was triggered by `createApiClient(...)` being called bare in each hook with no `useMemo` — every render produced a new client; every hook holding it re-fired; queries re-armed; loop.

This design's invariants and their consumers:

| Invariant | Owner | Consumers | Stability mechanism |
|-----------|-------|-----------|---------------------|
| `useApiClient` returned client | `lib/useApiClient` | Every mutation + every query in the modal AND in the canvas | UNCHANGED — `useApiClient` was hardened in PR #40; this design does not touch it. |
| `xyNodes` array | `WorkspaceCanvas` `useMemo([visibleDetails])` | `useNodesState`, `setNodes` effect, ReactFlow render | UNCHANGED in identity rules. `onPeek` is added to each node's `data` payload (object literal). The literal IS recreated per render — but `visibleDetails` itself is memoised on query result identity. As long as `visibleDetails`'s reference is stable (the existing query-result identity), the `xyNodes` memo stays stable. Adding a stable `onPeek` reference to the data payload does NOT churn the array reference. **Check:** Pierre must spread `onPeek` into the data payload inside the `useMemo`, not after — otherwise the memo dep set excludes it and an `onPeek`-only change would not refresh. Since `onPeek` IS stable, this is academic; the memo never needs to refresh on it. |
| `onPeek` callback | `usePeekState` in `WorkspacePage` | `WorkspaceCanvas` prop → every `NodeCardData.onPeek` → `NodeCardRenderer.handleClick` | `useCallback` with `[setSearchParams]`; `setSearchParams` is stable per react-router-dom 7. The reference does not change across `WorkspacePage` re-renders. |
| `nodeTypes` / `edgeTypes` | module-level constants | ReactFlow | UNCHANGED. |
| `viewportRef` | `WorkspaceCanvas` `useRef` | `handlePaneClick`, `handleCanvasDrop` | UNCHANGED. |
| Peek state `peekId` | `usePeekState` (URL-derived) | `WorkspacePage` re-render → `WorkspaceNodePeekModal` open prop | A `peekId` change re-renders `WorkspacePage` and the modal. **It does NOT cascade into `WorkspaceCanvas`** because `WorkspaceCanvas`'s only prop is `onPeek`, which is stable. React's same-input-same-output check on `WorkspaceCanvas` props means the canvas does not re-render on peek-state changes. |
| Modal-internal queries | `NodeDetailView` consuming `useNode/useNodeContent/useNodeListLinkedTo` | The modal | These queries mount on open, unmount on close. They do NOT share TanStack `queryKey`s with the canvas's viewport query (`['nodes', 'viewport', ...]`). Their mount/unmount cycle is local to the modal subtree and does not invalidate or refetch any canvas-side query. The only **inbound** signal from the modal to the canvas is via cache invalidation on mutation success (§6.5), which is the same prefix-broadcast that already works. |

**Why this does not re-introduce the PR #40–#42 class:**

The PR #40 incident was: a shared client object was unstable, AND multiple consumers shared it AND each consumer re-armed on identity change. Three conditions in conjunction.

Here:
- `useApiClient` (the actually-shared client) is unchanged — already memoised.
- `onPeek` is the only new shared callback. It has exactly ONE consumer chain (`WorkspaceCanvas` → `NodeCardData.onPeek` → `NodeCardRenderer`). Its identity is stable. Even if its identity churned, it would not re-arm any query — it is only ever called from a click handler, never used as a `useEffect` dep, never used as a `queryFn` identity.
- `peekId` is a brand-new piece of state, but it lives at `WorkspacePage` only. Its changes are scoped to that subtree. The canvas component is rendered as a sibling and is not affected by peek-state changes because it does not consume `peekId`.

The escape-from-container test from #271: "would a future copy of this pattern propagate?" — the only thing being copied here is `onPeek` plumbing into a single `data` payload field. The pattern does not generalize to other queries / mutations / hooks; it is a single-purpose callback wiring. Consumer count is 1 (the renderer) and is structurally bounded by the canvas's design.

**Concrete lifecycle on a peek-open click:**

1. Click handler fires inside `NodeCardRenderer` (event handler — not a render). React schedules nothing in the canvas.
2. `onPeek(id)` → `openPeek(id)` → `setSearchParams(...)`.
3. React-router updates the URL and re-renders the route subtree.
4. `WorkspacePage` re-renders. `usePeekState` returns a new `peekId`. The two callbacks (`openPeek`, `closePeek`) have stable identities (per `useCallback`), so the `<WorkspaceCanvas onPeek={openPeek} />` element receives the same prop reference. **The canvas component re-renders ONCE because its parent re-renders, but React's reconciliation sees identical props and bails before running effects** — except the new render pass evaluates the component function once, which is fine and unavoidable. No effect re-runs. No memo invalidates. xyflow does not re-mount nodes.
5. `<WorkspaceNodePeekModal peekId=42 onClose=... onPeekChange=... />` receives a new `peekId` and Radix transitions `Dialog.Root` from closed to open. The modal subtree mounts.
6. `NodeDetailView` mounts, its hooks register.

No loop class is reintroduced. The render harness at `frontend/src/test/setup.ts` (which promotes "Maximum update depth exceeded" to test failure) will catch any regression Pierre's implementation accidentally introduces.

---

## 10. Behavioural Contract (for Pierre's test layer)

Pierre selects the test surfaces per §13.1 of #420 (load-bearing). This design states the **behavioural** invariants those tests must collectively pin:

1. Clicking a node card on the workspace canvas opens a modal containing `NodeDetailView` (not via route navigation — the canvas component is NOT unmounted across the open/close cycle).
2. Closing the modal (any of: ESC, overlay click, X button, browser back) removes `?peek` from the URL and unmounts `NodeDetailView`; the canvas remains mounted with its viewport intact.
3. Mutating from inside the modal (Edit / Delete / Link / Unlink) propagates to the canvas without modal-or-canvas re-mount, via the existing prefix invalidation on `['nodes', 'viewport']` (which `usePatchNode` and `useDeleteNode` gain per §6.5).
4. Deep-link entry to `/workspace?peek=42` opens the modal on first render with no manual click.
5. Clicking a neighbour row inside the modal swaps the peek (modal stays open; `nodeId` prop changes; new queries fire).
6. The existing `/nodes/{id}` route is unchanged — it renders `NodeDetailView` with `variant="route"` and the back button behaves as today.
7. The "Maximum update depth exceeded" sentinel in `setup.ts` stays green on a peek-open → peek-close cycle and on a peek-swap.

Test mechanics: Pierre may use the canvas/modal harness already in place for `WorkspacePage` tests; the fiber-walk ban (§13.4 of #420) applies — pin behaviour via real DOM events, not by reaching into xyflow's memoised props.

---

## 11. Cross-Cutting Concerns

- **Security:** The backend is the security boundary (per §5.2 of `NodeDetailPage.tsx`'s existing comment). Write affordances are hidden when `whoami.permissions` lacks `write`; this is UX only. The modal inherits this via `NodeDetailView`.
- **Accessibility:**
  - Radix Dialog handles `role="dialog"`, `aria-modal="true"`, focus trap, focus return, ESC binding. The existing dialogs already use this; the peek modal uses the same primitive.
  - The modal MUST have a `Dialog.Title` and a `Dialog.Description` (Radix warns if either is missing). `NodeDetailView` cannot own these (a route container can't have a dialog title) — `WorkspaceNodePeekModal` provides them: title is the node name (or "Loading..." pre-fetch); description is a sr-only "Detail view for node N".
  - Neighbour rows in the modal variant are buttons (not anchors). They expose `aria-label="Open peek for node N"` and respond to Enter / Space.
- **Keyboard:**
  - ESC closes the modal. Radix's ESC handler stops propagation before xyflow sees it. No collision with `deleteKeyCode="Delete"`.
  - Tab cycles within the modal (Radix focus trap).
  - On the canvas, the existing `Enter`/`Space` keydown on `NodeCardRenderer` now opens the peek (because `handleClick` calls `onPeek`). Same gesture as click.
- **Error handling:** Existing `DivoidApiError` toast surfaces in `NodeDetailView` cover modal-side errors. 404 inside the modal renders the existing "Node N not found" inline state.
- **Observability:** No new logging. The modal's queries inherit TanStack Query's devtools surface. Pierre may add a `data-testid="workspace-peek-modal"` to ease test selection.
- **Concurrency:** A peek-swap that fires while a previous `useNode` is in flight is handled by TanStack Query's natural request cancellation per `queryKey`. The `useNode(42)` and `useNode(43)` requests have distinct keys; the React-side commit always shows the latest committed `nodeId`'s data.

---

## 12. Quality Attributes & Trade-offs

| Attribute | How addressed |
|-----------|---------------|
| **Maintainability** | Single extraction (`NodeDetailView`) replaces a code-duplication candidate. The modal is ~80 lines of Dialog scaffolding around the existing view. No new patterns, no new dependencies. |
| **Performance** | The modal mounts its three queries only when open. Closed peek = zero query cost. The canvas viewport query is unaffected by peek state. |
| **Accessibility** | Radix Dialog is already validated in the existing dialogs (Edit / Delete / Link). Reuse > reinvent. |
| **Render-stability** | See §9 — explicit lifecycle and invariant table, anchored to #271. |
| **URL discipline** | `?peek=<id>` follows the precedent set in #420 §7.3 (URL as source of truth for shareable views). |

### Trade-offs made

1. **Query-param URL strategy (`?peek=42`) chosen over modal-route (`/workspace/peek/:id`)**.
   - Query-param: simpler to integrate (no route addition), naturally cascades with workspace filters that may later need URL state, easy to support deep links via `useSearchParams`.
   - Modal-route: would compose better with future "peek a node from /search" generalization, but YAGNI per #1136 §1 — the user asked for peek-in-workspace, not peek-everywhere.
   - **Picked:** query-param. The future-generalization risk is acknowledged; if the same pattern ever needs to live on `/search` or `/wiki`, a follow-up can hoist `usePeekState` into a shared hook keyed off the current route.

2. **Single shared `NodeDetailView` chosen over duplicating the view into a `WorkspaceNodePeekContent` component**.
   - Sharing: one source of truth; modal and route are guaranteed to stay in sync.
   - Duplication: would diverge over time; bug fixes would need to land twice; #1136 §1 (DRY) violation.
   - **Picked:** sharing. The two-callback prop seam is small.

3. **Peek-swap REPLACES the current modal (collapses-and-reopens), no stack**.
   - Stack: would enable "back through nested peeks", but #1253 explicitly scoped this out for v1.
   - **Picked:** swap. Browser back-button gives a usable back-through-peek-trail because each `openPeek` pushes a history entry.

4. **`usePatchNode` and `useDeleteNode` gain `['nodes', 'viewport']` invalidation**.
   - Without it: editing a node's name inside the modal would not refresh the card on the canvas. The modal would close, the canvas card would still say the old name until the user pans.
   - With it: trivial one-line addition, consistent with `useLinkNodes`/`useUnlinkNodes` precedent from PR #124.
   - **Picked:** add the invalidation. It also incidentally benefits the route variant (a user editing on `/nodes/{id}` then visiting `/workspace`).

### Alternatives rejected with reasoning

- **Side-drawer/sheet:** rejected by user wording ("modal window") and by precedent in #299.
- **In-card content peek as a substitute:** different shape; that's #290.
- **Nested-modal stack (peek-A → peek-B-over-A):** explicitly out of scope in #1253.
- **Separate `Route<Modal>` pattern with `react-router-dom@7`'s background-location trick:** rejected because the existing routes file is a flat list; introducing background-location plumbing for one modal violates #1136 §4 ("can it be merged with something existing?").
- **State-on-`WorkspaceCanvas` rather than `WorkspacePage`:** rejected because peek-state changes would force the canvas to re-render, defeating the §9 stability argument. Hoisting peek state to `WorkspacePage` keeps the canvas inert across the open/close cycle.

---

## 13. Risks & Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| `propsAreEqual` in `NodeCardRenderer` accidentally compares `data.onPeek` and breaks memo when Pierre adds the field. | Low | Low | §5.6 explicitly states `propsAreEqual` is NOT extended. Pierre's test for "canvas does not re-render on peek open" (Item 1 from §10) pins this. |
| `setSearchParams` from `react-router-dom@7` has a subtle re-render quirk on consecutive calls. | Low | Low | The peek flow makes ONE setSearchParams call per user action. No batching needed. |
| Radix Dialog's default `Dialog.Title` warning is a runtime console-error and the test harness might catch it. | Low | Low | The modal provides `Dialog.Title` (node name or "Loading...") and a sr-only `Dialog.Description` (§11). |
| Nested Radix dialogs (peek modal + inner Edit dialog) double-trap focus. | Low | Low | Radix supports nesting natively (each Dialog Portal manages its own trap); the existing app does not currently nest dialogs, but the Radix docs document and the Radix tests validate the nesting. Pierre to verify with a manual click-through during implementation; if a focus bug surfaces, the fix is to add `Dialog.Root modal={false}` on the inner Edit dialog (loses Radix's overlay click-outside semantics — acceptable per the user's spec). |
| User edits inside the modal, mutation succeeds, but the modal-internal `useNode(id)` doesn't refresh because `usePatchNode` already invalidates `['nodes', id]` — but on close the modal unmounts and the data is fine; this is a "modal stays open and shows stale-then-fresh" UX detail. | Low | Cosmetic | TanStack Query's prefix invalidation refetches the modal's `useNode(id)` immediately on `usePatchNode.onSuccess`. The modal sees the new name within one round-trip. No design change. |
| Browser back from a peek-swap returns to a previous peek rather than closing the modal — could confuse some users. | Low | Low (acceptable per #1253 wording of "browser back closes the modal without leaving the workspace") | The first `openPeek` pushes a history entry from "no peek" → "peek=N". Browser back from the swap goes back to the first peek, NOT the no-peek state. The user gets a peek-trail back-button. Pressing back enough times eventually reaches the no-peek workspace. This matches the user's framing; if it ever feels wrong, Pierre can switch peek-swaps to `{ replace: true }` (see §6.3). |
| xyflow's onPaneClick (click on empty canvas) fires when the user closes the modal by clicking near the edge of the modal overlay — could accidentally open `CreateNodeDialog`. | Low | Annoyance | Radix Dialog overlay stops `pointerdown` propagation; xyflow's `onPaneClick` is bound on the canvas and does not fire from clicks in the portaled overlay (which lives at `document.body`). Pierre to verify with a manual click test. |

---

## 14. Migration / Rollout Strategy

Atomic deploy, no migration. The single PR Pierre delivers:

1. Adds `NodeDetailView.tsx` and moves the four-region body of `NodeDetailPage.tsx` into it.
2. Refactors `NodeDetailPage.tsx` to a thin shell that renders `NodeDetailView variant="route"` plus the back button.
3. Adds `WorkspaceNodePeekModal.tsx` and `usePeekState.ts`.
4. Modifies `WorkspacePage.tsx` to mount peek state and the modal sibling.
5. Modifies `WorkspaceCanvas.tsx` to accept `onPeek` and thread it through `xyNodes`.
6. Modifies `NodeCardRenderer.tsx` to call `data.onPeek` instead of `useNavigate`.
7. Modifies `mutations.ts` to add `['nodes', 'viewport']` invalidation to `usePatchNode` and `useDeleteNode`.
8. Adds load-bearing tests per §10.

A user-facing rollback (if the feature ever needed to be turned off) would require a code revert; there is no feature flag. The user explicitly asked for the feature, and feature flags here would be #1136 §3 YAGNI.

---

## 15. Open Questions

None that block implementation. The few small judgement calls Pierre may make at implementation time without coming back:

- Whether to keep the `variant` prop on `NodeDetailView` or branch purely on callback presence (§5.1.2). Either is in spec.
- Whether peek-swap uses `replace: true` or `replace: false` for history-entry shape (§6.3). The contract is "swap not stack"; the history trail is tunable.
- Whether the canvas error boundary wraps the modal as well (§5.4). Default: no. Either is acceptable.
- The exact pixel max-width of the peek modal. Suggest `max-w-3xl` (slightly wider than `EditNodeDialog`'s `max-w-md`, since the modal renders the full four-region detail view) — Pierre to confirm against the existing dialog idiom.

---

## 16. Implementation Guidance for the Next Agent

Recommended order (each step is independently reviewable):

1. **Extract `NodeDetailView`** from `NodeDetailPage`. Move every region (metadata, content, neighbours, dialogs). Accept `nodeId`, `variant`, optional `onClose`, optional `onNeighbourClick`. Leave the route shell in place rendering `<NodeDetailView nodeId={nodeId} variant="route" />`. Verify the existing `/nodes/{id}` route still works identically. This is the riskiest extraction step; do it first and prove no regression.
2. **Add `usePeekState`** at `frontend/src/features/workspace/usePeekState.ts`. Read/write `?peek` from `useSearchParams`.
3. **Add `WorkspaceNodePeekModal`** at `frontend/src/features/workspace/WorkspaceNodePeekModal.tsx`. Radix Dialog wrapping `NodeDetailView variant="modal"`.
4. **Wire `WorkspacePage`** to mount the modal as a sibling of the canvas, hand `openPeek` to the canvas and `{ closePeek, openPeek }` to the modal.
5. **Modify `WorkspaceCanvas`** to accept `onPeek` and thread it through `xyNodes`'s `data` field.
6. **Modify `NodeCardRenderer`** to call `data.onPeek(data.id)` in `handleClick`; remove `useNavigate`.
7. **Add `['nodes', 'viewport']` invalidation** to `usePatchNode.onSuccess` and `useDeleteNode.onSuccess` in `frontend/src/features/nodes/mutations.ts`.
8. **Add load-bearing tests** per §10. Use real DOM events (per #420 §13.4); pin against the render-loop sentinel in `setup.ts`.
9. **Manual smoke**: open a peek, edit name, close, see the card with new name; delete from inside, see the card disappear; deep-link a peek URL; click a neighbour, see swap; ESC closes; browser back closes.

Verify against #420 §15 Pre-PR Checklist before opening the PR. The modal lives under the workspace feature folder per #420 §1.1.

---

## 17. References

- **DiVoid #1253** — task that scopes this work.
- **DiVoid #1136** — Design Contracts. Pre-Design Checklist applied throughout this doc.
- **DiVoid #1166** — citation rule for #1136 on architect briefs.
- **DiVoid #1165** — design-and-implementation PR-shape rule (this design ships in its own PR per the orchestrator's brief; implementation follows).
- **DiVoid #1184** — anti-complexity rule (no seeded design questions; no audit columns; no hypothetical futures). Applied in §2's anti-scope list.
- **DiVoid #299** — PR 4b click-a-node decision; deferral lifted by this design.
- **DiVoid #283** — Workspace mode architecture; not regressed by this design (§4, §9).
- **DiVoid #271** — Render-stability rule; this design's binding constraint, demonstrated in §9.
- **DiVoid #290** — Sibling deferred task (in-card content peek). Out of scope for this design.
- **DiVoid #420** — Frontend code contracts; Pierre must satisfy on implementation.
- **PR #124** — `['nodes', 'viewport']` invalidation prefix; relied on in §6.5.
