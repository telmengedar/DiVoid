# Architectural Document: Open Node by ID — search-by-id + workspace in-canvas search

**Author:** Sarah (software architect)
**Date:** 2026-06-04
**Status:** Proposed — `/search` increment shipped in this PR; workspace surface filed as a follow-up Pierre PR
**Task:** DiVoid node **#1604**
**Subsumes deferral from:** DiVoid node **#293** (workspace search deferral — the workspace-surface portion of this design lifts the deferral; the deferral node closes when the follow-up PR lands)
**Code contracts:** Frontend code contracts node **#420**; Design contracts node **#1136**
**Render-stability rule:** DiVoid node **#271** (binding constraint — applies to the workspace surface in PR 2 of the rollout, not to the `/search` surface in this PR)
**Peek modal foundation:** DiVoid node **#1254** (the `/workspace?peek=<id>` mechanism this design leans on)
**Workspace baseline:** DiVoid node **#283** (workspace mode — must not regress)

---

## 0. User words

Verbatim, Toni 2026-06-04:

> "i need a way to open nodes by id - often enough a coworker just mentions an id and the ui has no direct way of searching a node by id.
> - there should be a way in search
> - a direct search in workspace would also help a lot (id, but direct semantic search would also be nice)"

The spec is two things:

1. **Add an "open by id" affordance to `/search`.**
2. **Add an in-canvas search affordance to `/workspace`** that supports id and (also nice) semantic.

Everything in this document tracks those two intents. Nothing else.

---

## 1. Problem Statement

Today, when a coworker mentions a node id in conversation (e.g. "see #1462"), the user has no direct path to that node:

- The `/search` page surfaces three retrieval modes (Semantic, Linked, Path). None of them takes "give me node N" as input. Linked walks neighbours; it does not open the anchor itself. The user has to hand-edit the URL to `/nodes/1462` to view a known id — a workflow Toni has called out as friction.
- The `/workspace` page surfaces only nodes currently in the viewport. A coworker-mentioned id might be off-canvas entirely. Search-within-workspace is deferred (#293).

The goal is a one-step path from "I know the id" to "I'm looking at the node," on both surfaces. The secondary workspace goal is semantic search inside the canvas.

Success criteria:

1. On `/search`, the user can type a numeric id and one action (Enter / click Open) takes them to the node detail view.
2. On `/workspace`, the user can type an id (or a semantic query) into an in-canvas input and one action surfaces the chosen node — opened as a peek modal via the existing `?peek=<id>` mechanism (#1254).
3. The existing `/search` semantic / linked / path tabs are unchanged.
4. The existing workspace canvas render-stability (#271 / PRs #40–#42 / #124) is preserved — the in-canvas input must not couple into the canvas's data path.
5. The two surfaces share the shared building blocks (`useNode`, `useNodeSemantic`, `usePeekState`) — no parallel hooks.

---

## 2. Scope & Non-Scope

### In scope

- A 4th tab on `/search`, **"By ID"**, with a numeric input and an Open button that navigates to `/nodes/{id}` on submit.
- A floating in-canvas search affordance on `/workspace`, top-right of the canvas overlay, with a single input that:
  - Treats pure-digit input as an id and opens the peek modal directly via `openPeek(id)` on Enter.
  - Treats non-digit input as a semantic query (debounced 250 ms) and shows up to 8 ranked matches in a dropdown. Selecting a match opens the peek modal via `openPeek(id)`.
- Inline validation on the id input: positive integer required; otherwise the Open button is disabled and an inline message guides the user.

### Explicitly out of scope (this design does not specify any of these)

1. **Multi-id batch open.** "Open ids 12, 45, 81 at once." The user did not ask.
2. **Type / status filters inside the in-canvas search box.** Those live in the existing workspace filter popovers.
3. **Search history persistence / autocomplete.** The user did not ask. KISS.
4. **Keyboard-shortcut palette (cmd-K).** Adjacent surface; not requested.
5. **Replacing the existing `/search` semantic / linked / path panels.** Only adding the id tab.
6. **Mini-map overlay (#289), per-user position overlays (#291).** Separate tasks.
7. **Pan / zoom-to-node in the workspace on id-jump.** See §6.4 for the decision; opening the peek modal is the canonical outcome. Panning is deliberately not added in this iteration.
8. **Modal-create-node fix (DiVoid #1606).** That is a separate parallel task being implemented by Pierre direct.
9. **Backend changes.** Pure frontend design.

This list is the §4-of-brief anti-complexity gate. None of the nine items has a user request behind it.

---

## 3. Assumptions & Constraints

| # | Assumption / Constraint | Confidence | Notes |
|---|---|---|---|
| A1 | `GET /api/nodes/{id}` already returns a `NodeDetails` for any id (or 404). `useNode(id)` is the canonical caller. | Hard | Verified by reading `frontend/src/features/nodes/useNode.ts`. |
| A2 | `usePeekState()` exposes `openPeek(id)` and `closePeek()` and `peekId`, backed by `?peek=<id>`. The modal mounts off-canvas at the `WorkspacePage` level. | Hard | Verified by reading `frontend/src/features/workspace/usePeekState.ts` and `WorkspaceNodePeekModal.tsx`. |
| A3 | `useNodeSemantic(query, filter)` is the canonical semantic search hook; gated on non-empty query; returns `Page<NodeDetails>` with `similarity`. | Hard | Verified by reading `frontend/src/features/nodes/useNodeSemantic.ts`. |
| A4 | `WorkspaceNodePeekModal` does NOT require the peeked node to be inside the current viewport. It re-fetches via `useNode(peekId)` and mounts `NodeDetailView` for any id, including ids whose canvas position is off-screen or never set. | High | Verified by reading the modal source — it depends on `useNode`, not on the viewport query. |
| A5 | `react-router-dom@7` `useNavigate` and `MemoryRouter` are available; tab state on `/search` is component-local `useState`. | Hard | Verified by reading `SearchPage.tsx`. |
| A6 | The canvas overlay top-left is taken by the filter toolbar (`WorkspaceFilterPopover`). The top-right is free for the new search affordance. | High | Verified by reading `WorkspaceCanvas.tsx:584-605`. |
| A7 | The render-stability invariants from #271 (workspace, fan-out) bind on the workspace surface. The `/search` surface has no canvas, no xyflow, no shared client beyond the standard `useApiClient` already used by every other search tab — adding a 4th tab does not extend a shared invariant. | High | The `/search` increment is therefore the low-risk first PR; the workspace surface goes second per §14. |
| A8 | When the user types a non-positive integer into either id input (zero, negative, or non-numeric), the input is rejected at the validation gate, the Open button is disabled, and no navigation / modal-open occurs. | Stated | Mirrors the existing `LinkedPanel` numeric input behaviour at `SearchPage.tsx:236-254`. |

---

## 4. Architectural Overview

Two surfaces, one shared peek-state primitive on the workspace side, and one shared `useNavigate` on the search side. The pieces look like this:

```
 /search route
 ┌───────────────────────────────────────────────────────┐
 │ SearchPage                                            │
 │  ├─ TabBar  [Semantic │ Linked │ Path │ By ID *new*]  │
 │  └─ panels:                                           │
 │      ├─ SemanticPanel  (unchanged)                    │
 │      ├─ LinkedPanel    (unchanged)                    │
 │      ├─ PathPanel      (unchanged)                    │
 │      └─ ByIdPanel  *new*                              │
 │           input(numeric) → Open ──► navigate(/nodes/N)│
 └───────────────────────────────────────────────────────┘

 /workspace route                                      *PR 2 of rollout*
 ┌──────────────────────────────────────────────────────────────────┐
 │ WorkspacePage                                                    │
 │  ├─ WorkspaceCanvas  (UNCHANGED — no new prop, no new effect)    │
 │  │   └─ top-right overlay slot reserved for child injection      │
 │  │                                                               │
 │  ├─ WorkspaceSearchBar  *new* (sibling of canvas, not a child)   │
 │  │   ├─ single input (id-or-query)                               │
 │  │   ├─ classify(input): digits → id-mode | else → semantic-mode │
 │  │   ├─ id-mode submit (Enter) → openPeek(id) from usePeekState  │
 │  │   └─ semantic-mode (debounced) → useNodeSemantic(q)           │
 │  │         dropdown: top 8 rows                                  │
 │  │         row click → openPeek(row.id)                          │
 │  │                                                               │
 │  └─ WorkspaceNodePeekModal  (UNCHANGED — already mounts peekId)  │
 └──────────────────────────────────────────────────────────────────┘
```

The `WorkspaceSearchBar` lives at the `WorkspacePage` level, **not** inside `WorkspaceCanvas`. This is load-bearing for §9 (render-stability) — the search bar's input state, debounce timer, and semantic-query result are scoped to itself, never cross the canvas's prop boundary.

---

## 5. Components & Responsibilities

### 5.1 `ByIdPanel` (NEW — co-located in `frontend/src/features/nodes/SearchPage.tsx`)

Single responsibility: render an id input + Open button, validate the input is a positive integer, navigate to `/nodes/{id}` on submit.

Owns:

- Local `useState<string>` for the raw input.
- `useNavigate()` from `react-router-dom`.
- The submit handler that parses, validates, and navigates.
- The disabled-state computation for the Open button.

Does NOT own:

- Any data fetch. The panel does not pre-validate the id exists on the backend — that's `NodeDetailPage`'s job. (Reason: KISS — the existing `/nodes/{id}` page already handles 404 with a clean message at `NodeDetailPage.tsx`. Pre-checking would duplicate that and force the user to wait for a roundtrip on every Open.)
- Tab orchestration. That's `SearchPage`'s job.

Placement: a co-located function component inside `SearchPage.tsx`, mirroring `SemanticPanel`, `LinkedPanel`, `PathPanel`. No new file — the three existing panels live in `SearchPage.tsx`, the fourth follows that convention. KISS.

### 5.2 `SearchPage` (MODIFIED — `frontend/src/features/nodes/SearchPage.tsx`)

Two delta changes:

- `Tab` union extends to include `'by-id'`.
- `TABS` array gains the fourth entry `{ id: 'by-id', label: 'By ID', Icon: Hash }` (Lucide `Hash` is already in the icon family used here).
- Conditional render: `{activeTab === 'by-id' && <ByIdPanel />}`.

Everything else is unchanged.

### 5.3 `WorkspaceSearchBar` (NEW — `frontend/src/features/workspace/WorkspaceSearchBar.tsx`) — PR 2 of rollout

Single responsibility: provide a floating overlay input that opens a peek modal for an id or for a semantic result.

Owns:

- Local `useState<string>` for the raw input.
- Local `useState<string>` for the debounced semantic query (only set when input is non-digit).
- Local debounce timer `useRef<ReturnType<typeof setTimeout>>`.
- `useNodeSemantic(debouncedQuery, { count: 8 })` — disabled when query empty.
- Submit handler (Enter): if digits-only → `openPeek(Number(input))`; otherwise → no-op (semantic results are click-to-select).
- Click-on-result handler: `openPeek(row.id)` + clear input.
- ESC handler: clear input + close any open dropdown.

Does NOT own:

- The peek state itself — that's `usePeekState`'s job.
- The peek modal — `WorkspaceNodePeekModal` already mounts it.
- The canvas — it does not consume `WorkspaceCanvas` props in any way.

Props:

- `onOpenPeek: (id: number) => void` — stable callback from `usePeekState.openPeek`. Provided by the parent (`WorkspacePage`) which already has `openPeek` in scope.

### 5.4 `WorkspacePage` (MODIFIED — `frontend/src/features/workspace/WorkspacePage.tsx`) — PR 2 of rollout

Adds one sibling component below `WorkspaceCanvas`:

```
<div className="h-full w-full relative">
  <WorkspaceCanvas onPeek={openPeek} />
  <WorkspaceSearchBar onOpenPeek={openPeek} />
  <WorkspaceNodePeekModal ... />
</div>
```

The search bar is positioned absolutely inside the relative container. `WorkspaceCanvas` is untouched — no new prop, no new effect.

### 5.5 Input classifier (utility — exported from `WorkspaceSearchBar.tsx`)

Single responsibility: classify a raw input string as `'id'` (pure positive digits), `'query'` (non-empty, contains non-digit), or `'empty'`.

Owns: the regex check (essentially `/^\s*\d+\s*$/` + positive check) and the trim.

This is a 6-line pure function, exported for unit-testability of the contract. It is NOT a separate file — co-located at the top of `WorkspaceSearchBar.tsx`.

---

## 6. Interactions & Data Flow

### 6.1 `/search` → By ID tab → Open

1. User clicks the "By ID" tab. `SearchPage` updates `activeTab` to `'by-id'`. `ByIdPanel` renders.
2. User types digits into the input. Local state updates. The Open button computes its disabled state from `parseInt(input) > 0`.
3. User clicks Open (or presses Enter inside the form).
4. Submit handler: parses, double-checks `> 0`, calls `navigate(ROUTES.NODE_DETAIL(id))`.
5. `/nodes/{id}` route mounts. The existing `NodeDetailPage` runs `useNode(id)`. If the id is valid, the detail renders. If not, the existing 404 path renders the same message any deep-link to a missing id gets.

No new API contract. No new error handling. The Open button is the only delta path.

### 6.2 Workspace → search bar → id-jump (PR 2)

1. User types `1462` into the search bar input.
2. Input classifier returns `'id'`. The semantic-dropdown stays unmounted.
3. User presses Enter.
4. Submit handler calls `onOpenPeek(1462)` → `usePeekState.openPeek(1462)` → `setSearchParams(..., {replace: false})` writes `?peek=1462`.
5. `WorkspacePage` re-renders. `WorkspaceNodePeekModal` sees `peekId === 1462`, opens the dialog, mounts `NodeDetailView`. **`WorkspaceCanvas` does NOT re-render** — the same render-stability story documented in #1254 §9.
6. If the id does not exist on the backend, `NodeDetailView`'s `useNode` returns 404 and renders the existing not-found state inside the modal. Same outcome as the existing peek modal behaviour for any missing id.

### 6.3 Workspace → search bar → semantic search (PR 2)

1. User types `auth flow design` into the search bar.
2. Input classifier returns `'query'`.
3. Debounce timer (250 ms) fires. `useNodeSemantic('auth flow design', { count: 8 })` runs. The dropdown renders 8 ranked rows below the input.
4. User clicks a row.
5. Click handler calls `onOpenPeek(row.id)` → same peek-modal path as 6.2.
6. The dropdown closes when the modal opens, OR when the input clears. (Implementation detail: clear input on row click closes the dropdown.)

### 6.4 Decision: id-jump opens the peek modal — it does NOT pan / zoom the canvas

The brief (§5) explicitly asks for the call:

> "Decide whether clicking a search result in the workspace opens the existing peek modal (#1254) or just pans the viewport. The user said 'open nodes by id' — the natural reading is the peek modal opens on the chosen node, mirroring the existing canvas-click flow. Confirm or rebut in §2 of your design."

**Decision: open the peek modal (#1254). Do NOT pan or zoom.**

Reasoning:

- **User words.** *"open nodes by id"* — the verb is *open*, the noun is *nodes*. The peek modal is the canonical "open this node" gesture on the workspace today (since #1254 landed). Panning is a different gesture (relocate the viewport).
- **The peek modal already works for any id.** `useNode(id)` does not require the node to be in viewport. So the modal-open path is a single-step `openPeek(id)` with no viewport coordination, no canvas re-render, no fetch-and-center dance.
- **Panning to an off-viewport node has a discoverability cost.** If a coworker mentions #1462 and the user types it, panning the canvas to (x, y) of #1462 leaves the user staring at one node in an unfamiliar region of the graph — they often still need to open it to see what it says. The modal already shows the full detail (incl. neighbour rows that link out — which is the workspace's exploration affordance anyway).
- **Render-stability cost.** Pan-on-jump would require either a `useReactFlow().setCenter` call from inside `WorkspaceSearchBar` (which means the search bar reaches into the xyflow store — a coupling I want to avoid in this design) or a new ref-forwarded handler on `WorkspaceCanvas` (which means a new prop on the canvas, a new fan-out point per #271). Both have a cost the user did not ask us to pay.

**What this design does NOT preclude.** If, after the modal-open behaviour ships, Toni observes "I want it to also pan," that is one additional capability with a clean seam — `usePeekState` could grow a `centerOnOpen` flag and `WorkspaceCanvas` could expose a stable `setCenter(id)` callback. Adding that later costs no rework here.

This is the call requested by the brief. Stated, justified, and the simpler shape (KISS / §1136 §4).

### 6.5 Validation of the id input

| Input | Classifier | UI state | Submit |
|---|---|---|---|
| `` (empty) | empty | Open disabled | no-op |
| `   ` (whitespace) | empty | Open disabled | no-op |
| `0` | id | Open disabled | no-op (positive guard) |
| `-3` | (whitespace+digits, but minus sign fails the regex) | empty | Open disabled |
| `12abc` | query (in workspace), invalid for `/search` ByIdPanel | Open disabled on `/search`; in workspace classifier returns `'query'` and the input falls through to semantic | no-op for `/search`; semantic for workspace |
| `1462` | id | Open enabled | navigate / openPeek |

`/search` ByIdPanel: input `type="number" min={1}` matches the existing `LinkedPanel` pattern at `SearchPage.tsx:238-245`. Submit disabled when `parseInt(input) <= 0 || isNaN(parseInt(input))`.

Workspace search bar: input `type="text"`. The classifier function is the only validation gate. Pure-digits enables Enter-to-open; otherwise the dropdown handles the user's intent.

---

## 7. Data Model (Conceptual)

No new entities. No new query keys.

- The `/search` ByIdPanel reuses `ROUTES.NODE_DETAIL(id)` and `useNavigate`.
- The workspace search bar reuses `usePeekState`, `useNode`, and `useNodeSemantic`. The semantic dropdown uses the existing `['nodes', 'semantic', query, filter]` cache key.

URL surface unchanged. The only URL state in play is the already-existing `?peek=<id>` on the workspace, written via `openPeek`.

---

## 8. Contracts & Interfaces (Abstract)

### 8.1 `ByIdPanel` (PR 1 of rollout)

- Inputs: none (no props).
- Outputs: a tab panel containing an `<input type="number" min={1}>` and an Open submit button.
- Invariants:
  - Open is disabled iff `parseInt(input.trim(), 10) <= 0 || isNaN(...)`.
  - Submit navigates to `ROUTES.NODE_DETAIL(parsedId)`.
  - Submit never makes a network request from inside the panel.

### 8.2 `WorkspaceSearchBar` (PR 2 of rollout)

- Inputs: `onOpenPeek: (id: number) => void` (stable from `usePeekState.openPeek`).
- Outputs: a floating overlay with input + (when query mode) dropdown.
- Invariants:
  - `WorkspaceCanvas` props are unchanged by mounting this component.
  - The semantic query is debounced (250 ms) before `useNodeSemantic` runs.
  - The dropdown contains at most 8 rows.
  - Row click calls `onOpenPeek(row.id)` and clears the input.
  - Pressing Enter with digit-only input calls `onOpenPeek(Number(input))`.
  - Pressing Enter with non-digit input is a no-op (the dropdown handles selection).
  - Pressing ESC clears input and unmounts the dropdown.

### 8.3 `classifyInput` (PR 2 of rollout, pure helper)

| Input shape | Return |
|---|---|
| Empty / whitespace-only | `'empty'` |
| Trimmed value matches `/^\d+$/` AND parses to > 0 | `'id'` |
| Anything else | `'query'` |

No side effects. Exported for unit test.

---

## 9. Cross-Cutting Concerns

### 9.1 Render stability (DiVoid #271 — binding constraint, applies to PR 2)

The workspace surface is the only render-stability concern. The `/search` surface has no shared invariants beyond `useApiClient` (already canonical) — a 4th tab is hygiene, not a fan-out.

For PR 2:

| Invariant | Owner | Consumers | Stability |
|-----------|-------|-----------|-----------|
| `useApiClient` client | `lib/useApiClient` | every query / mutation | UNCHANGED |
| `xyNodes` array | `WorkspaceCanvas` useMemo | useNodesState, ReactFlow | UNCHANGED |
| `onPeek` callback | `usePeekState` in `WorkspacePage` | canvas → NodeCardData → renderer **AND** new `WorkspaceSearchBar` | stable (already established in #1254 §9); adding a second consumer does not change the identity |
| `peekId` | `usePeekState` (URL-derived) | `WorkspaceNodePeekModal` | UNCHANGED — does NOT cascade into canvas |
| `WorkspaceSearchBar` local state | `WorkspaceSearchBar` itself | self only | scoped — does not cross any component boundary |
| Semantic query cache | TanStack Query | `WorkspaceSearchBar` | scoped — `useNodeSemantic` already memoised; cache key is `['nodes', 'semantic', q, filter]` |

**Why this does not re-introduce the PR #40–#42 class:**

- `onPeek` is the only invariant that gains a consumer. It is referentially stable (`useCallback([])` with ref-stable `setSearchParams` per `usePeekState.ts:60-75`). Adding the search bar as a second consumer is a flat fan-out — no effect dependencies, no memoisation chain.
- The search bar's debounce timer and input state never escape its function body.
- `useNodeSemantic` is the same hook used on `/search` today; its query-key shape and stable-params memoisation are already proven.

**Concrete lifecycle on a workspace id-jump:**

1. Type `1462` into search bar. Local input state updates. **Canvas does not see this.**
2. Press Enter. Search bar calls `onOpenPeek(1462)`.
3. `openPeek` calls `setSearchParams({ peek: '1462' })` — non-replace push.
4. React-router updates URL. `WorkspacePage` re-renders. **Stable callbacks; `<WorkspaceCanvas onPeek=...>` receives identical prop reference. Canvas function evaluates once (unavoidable), no effects re-run, no memo invalidates, xyflow does not re-mount nodes.**
5. `WorkspaceNodePeekModal` receives new `peekId`. Radix transitions open. `NodeDetailView` mounts; queries fire.

The render-loop sentinel at `frontend/src/test/setup.ts` (which promotes "Maximum update depth exceeded" to test failure) is the catch-all for accidental regression. Existing render-loop tests (`WorkspacePage.renderLoop.test.tsx`) cover the canvas; the new search bar gets its own render-loop assertion in PR 2's test list (§13.2 below).

### 9.2 Error handling

| Failure | Behaviour |
|---|---|
| `/search` ByIdPanel: user types non-positive | Open stays disabled. No toast (over-noisy; the disabled button is feedback enough). |
| `/search` ByIdPanel: user navigates to a non-existent id | Existing `NodeDetailPage` 404 path renders. Untouched. |
| Workspace id-jump: peek modal opens, `useNode(id)` 404s | Existing `NodeDetailView` 404 path renders inside the modal. Untouched. |
| Workspace semantic: `useNodeSemantic` errors | Existing error handling in the hook (DivoidApiError toast surfaces from the hook caller — match `SearchPage.tsx:132-136` pattern; search bar does the same). |
| Workspace semantic: query is empty / digits-only | Hook is gated off (its `enabled` is `query.trim().length > 0`). No fetch. |

### 9.3 Accessibility

- `/search` ByIdPanel: the input has `aria-label="Node ID to open"`; the Open button has visible text. Mirrors `LinkedPanel`.
- Workspace search bar: the input has `aria-label="Search by ID or query"`. The dropdown uses `role="listbox"` with `aria-activedescendant`; rows are `role="option"` and keyboard-navigable with Arrow Up/Down + Enter. ESC dismisses.

### 9.4 Observability

No new logging. The existing `lib/api.ts` debug-log path already covers `useNodeSemantic` and `useNode`.

### 9.5 Theming

Both surfaces use existing Tailwind primitives. The workspace search bar's overlay positioning mirrors the existing filter popovers' visual language (background, border, shadow).

---

## 10. Quality Attributes & Trade-offs

| Attribute | Approach | Trade-off |
|---|---|---|
| Maintainability | Reuse `useNode`, `useNodeSemantic`, `usePeekState`. No new hooks, no parallel logic. | None. |
| Performance | The `/search` increment is render-only. The workspace semantic dropdown is debounced 250 ms. | The debounce is a magic number; documented inline. Operator-tunable later if needed (KISS — no config knob now, per #1136 §3). |
| Accessibility | Standard input + Open pattern on `/search`; listbox+option on the workspace dropdown. | None. |
| Render-stability | PR 1 is hygiene. PR 2 mounts a sibling, never touches canvas props. | None. |
| URL-source-of-truth (#420 §7.3) | The id-jump on workspace pushes `?peek=<id>` (already URL state). The `/search` Open navigates to `/nodes/{id}` (already URL state). | None — both surfaces stay URL-first. |

### Trade-offs made explicit

1. **4th tab on `/search` over an inline always-visible id input.** A 4th tab matches the existing TabBar pattern (Semantic / Linked / Path) and is visually consistent. An inline always-visible "Or open by ID:" form above the tabs would be ~5 fewer lines of code but would clutter the page and feel grafted on. The tab cost is ~30 lines of code (mirrors `LinkedPanel`). Decision: tab. Net: a clean, discoverable place for the function.

2. **Sniffed-mode input on workspace over an explicit mode toggle.** A single input that classifies digit-only as id and otherwise as semantic is what the user asked for ("id, but direct semantic search would also be nice"). An explicit toggle (radio buttons / segmented control) would be more discoverable but heavier — and the disambiguation is obvious from typing ("1462" is clearly an id; "auth flow" is clearly a query). Decision: sniffed mode. If users discover the dual-mode is confusing, the next iteration can add a hint label below the input.

3. **No pan / zoom on id-jump (§6.4).** Stated and justified above. The simpler shape — open the peek modal — fully satisfies the user's verb ("open"). Adding pan is a clean seam later if needed.

4. **Bundled increment is `/search`, not workspace.** Per brief §5 and §14 below, the workspace surface is the higher-risk increment (render-stability constraint); the `/search` increment is the lowest-risk and shipping it first gets the user the id-open path on the surface they already use most. Workspace is filed as a follow-up Pierre task in DiVoid linked to this design.

### DRY check (#1136 §1, #1267)

- `ByIdPanel` and `LinkedPanel` both have a numeric-input + submit pattern. Block size: ~5 lines (the input + the disabled-state check + the submit handler). Site count: 2. `5 × 2 = 10`, below the ~15-20 threshold from #1267. Inline is correct; no extraction.
- The workspace search bar and `/search` ByIdPanel do NOT share code: workspace classifies-then-routes-to-semantic-or-id; `/search` ByIdPanel is id-only. The semantic side of the workspace search bar uses `useNodeSemantic` directly, the same hook the `/search` SemanticPanel uses — no duplication.

### KISS check (#1136 §1, #4)

- ByIdPanel: can-it-be-deleted? No — the panel is the feature. can-it-be-merged-with-LinkedPanel? No — Linked walks neighbours, ByID opens the anchor; conflating them would force a mode toggle inside one panel that the user did not ask for. can-it-be-inlined? No — the three sibling panels are already separate function components; inlining would break symmetry.
- WorkspaceSearchBar: can-it-be-deleted? No — feature. can-it-live-inside-WorkspaceCanvas? **No** — that violates §9 render-stability. can-it-be-inlined-into-WorkspacePage? No — the input + dropdown + classifier is ~80 lines; inlining would bloat `WorkspacePage` past readability. Component is justified.
- `classifyInput`: can-it-be-inlined? Yes (3-line check at the top of the file). But exporting it lets the contract get a load-bearing unit test in PR 2 without rendering the full bar. Worth ~6 lines for a tested seam. **Decision: export as a pure helper at the top of `WorkspaceSearchBar.tsx`**.

### YAGNI check (#1136 §1)

- No feature flag. No config knob. No "for future" hooks.
- The 250 ms debounce is a `const` inside the file (not config). If operators ever want to tune it, the change is one line — no audit column, no telemetry compound (avoids the §3 anti-pattern).
- No search-history persistence, no autocomplete cache, no recent-ids list. The user did not ask.

### Alternatives rejected

- **Inline id input above the tabs on `/search`.** Cluttery; rejected for tab.
- **Cmd-K palette across the entire app.** Out of scope; the user asked for two specific surfaces.
- **Backend search-by-id endpoint.** `GET /api/nodes/{id}` already exists; no new endpoint needed.
- **Pan + open on workspace id-jump.** §6.4 — rejected, clean seam left.
- **Explicit mode toggle on workspace search.** Heavier and unnecessary; sniffed mode is unambiguous.
- **A separate `useNodeById` hook for the workspace.** `useNode(id)` is already the canonical id-fetcher; `openPeek(id)` triggers it inside the modal. No new hook.

---

## 11. Risks & Mitigations

| # | Risk | Impact | Mitigation |
|---|---|---|---|
| R1 | User on `/search` types an id that doesn't exist; navigates to `/nodes/{id}`; sees the 404 page. | UX speed-bump. | `NodeDetailPage` already handles missing-id gracefully. No mitigation needed beyond the existing path. |
| R2 | User on workspace id-jumps to a node off the current viewport; expects the canvas to pan; sees only the modal. | Mismatched expectation, ~minor. | §6.4 documents the decision. If feedback materialises, panning is one config away (see §6.4). |
| R3 | Workspace search bar's dropdown overlaps the type-filter popover when both are open. | UI overlap. | Position the search bar in the top-right of the canvas overlay; type-filter is top-left. No collision. |
| R4 | Workspace semantic search returns ids that are not positioned on the canvas — opening their peek modal works but the user has no spatial anchor when they close it. | Minor UX. | Same condition exists for any peek opened by URL deep-link today; the user is left where they were on close. Acceptable. |
| R5 | Sniffed-mode classifier confuses users (someone types "123abc" and expects id-mode). | Minor UX. | The classifier is documented in §8.3. The dropdown immediately surfaces semantic results on non-digit input; the user sees the response and adapts. |
| R6 | Workspace `WorkspaceSearchBar` mounts a `useNodeSemantic` query and re-runs it on every keystroke without debounce. | Backend load spike. | The debounce gate is the load-bearing primitive; a unit test pins it (§13.2 T3). |

---

## 12. Migration / Rollout Strategy

Two PRs, sequenced low-risk-first.

### PR 1 — `/search` By ID tab (THIS PR)

Bundled with this design doc per #1165. Branch off `origin/main` (fresh; PR #133 merged 2026-06-04). Closes the `/search` portion of DiVoid #1604.

Order of operations within PR 1:

1. Commit the design doc at `docs/architecture/open-by-id.md`.
2. Extend `Tab` union and `TABS` array in `SearchPage.tsx`.
3. Add `ByIdPanel` function component co-located in `SearchPage.tsx`.
4. Wire the conditional render in the page body.
5. Add a load-bearing test for the ByIdPanel submit behaviour (see §13.1).
6. Open PR with title `feat(search): add open-by-id tab on /search (DiVoid #1604)` and body referencing this design + the follow-up workspace task.

### PR 2 — Workspace in-canvas search bar (FOLLOW-UP, Pierre)

Branch off `main` *after PR 1 merges*. Filed as DiVoid task linked to this design. Closes the workspace portion of #1604 AND lifts the deferral at #293.

Order of operations within PR 2:

1. Add `WorkspaceSearchBar.tsx` with the `classifyInput` helper exported.
2. Mount the search bar as a sibling of `WorkspaceCanvas` inside `WorkspacePage`. Inject `openPeek` as the `onOpenPeek` prop.
3. Style the floating overlay (top-right, matches filter popover visual language).
4. Add load-bearing tests per §13.2.
5. Open PR referencing this design.

### Why two PRs, not one

- The `/search` increment is render-stability-irrelevant. It can land independently and gives the user the most-used id-open path today.
- The workspace surface mounts inside the canvas-bearing route. Even though the design isolates it from the canvas (sibling, not child), bundling it doubles the surface area of a single review and increases the chance of an iteration cycle on the workspace half blocking the `/search` half. Per orchestrator-side PR-scope discipline (CLAUDE.md "PR scope: one feature per PR"), the two units ship separately.
- The user named the `/search` surface first in their request ("there should be a way in search") and the workspace surface as the secondary ("a direct search in workspace would also help a lot"). Ordering follows.

---

## 13. Load-Bearing Tests (per #275 / #420 §13.1)

### 13.1 PR 1 — `/search` ByIdPanel

| # | Test | Negative proof (must FAIL if reverted) | Positive proof |
|---|---|---|---|
| ST1 | Switching to "By ID" tab renders an input with `aria-label="Node ID to open"` and a disabled Open button. | Remove the `<input>` or the disabled-when-empty guard — assertion fails. | Both elements render; button starts disabled. |
| ST2 | Typing `1462` enables Open. Clicking Open calls `navigate` with `/nodes/1462`. | Remove the `navigate(ROUTES.NODE_DETAIL(parsed))` line — assertion fails. | `navigate` was called with `/nodes/1462`. |
| ST3 | Typing `0` keeps Open disabled. | Remove the `parsed > 0` guard — Open becomes enabled and the test fails. | Open remains disabled. |
| ST4 | Typing non-digits (e.g. `abc`) leaves the input rejecting input (browser-level for `type="number"`) and Open disabled. | Remove the `isNaN` guard — Open enables on the empty parse, navigation goes to `/nodes/NaN`. | Open stays disabled. |

Tests live at `frontend/src/features/nodes/SearchPage.test.tsx` (extending the existing test file). Mocks for `react-router-dom`'s `navigate` already use the existing pattern there.

### 13.2 PR 2 — workspace search bar (filed in the follow-up task)

| # | Test | Negative proof | Positive proof |
|---|---|---|---|
| WT1 | Typing `1462` and pressing Enter calls `onOpenPeek(1462)`. | Remove the Enter handler — spy never fires. | Spy fires with `1462`. |
| WT2 | Typing `abc` and pressing Enter does NOT call `onOpenPeek`; the semantic query fires after 250 ms. | Remove the debounce — query fires immediately on first keystroke. | After 250 ms, MSW handler captures the request. |
| WT3 | `classifyInput('1462')` → `'id'`; `classifyInput('auth flow')` → `'query'`; `classifyInput('  ')` → `'empty'`. | Flip the regex anchors or remove the trim — assertion fails. | All three cases match. |
| WT4 | Mounting `WorkspaceSearchBar` next to `WorkspaceCanvas` does NOT cause the canvas to re-render (consumer count on the canvas does not change). | Refactor so the search bar passes its input state to the canvas — render-count assertion fails. | Canvas render count is 1 over mount + initial-data-arrival cycle. |
| WT5 | Clicking a dropdown row calls `onOpenPeek` with that row's id and clears the input. | Remove the click handler's `setInput('')` line — input retains value, dropdown stays open. | Input clears, dropdown unmounts. |

Tests live in `frontend/src/features/workspace/WorkspaceSearchBar.test.tsx` (NEW) and `classifyInput.test.ts` (NEW). The render-stability test (WT4) follows the `WorkspacePage.renderLoop.test.tsx` MAX_RENDERS sentinel pattern (#420 §13.7).

---

## 14. Open Questions

None block PR 1. For PR 2, Pierre may make these calls without coming back:

- **Q1.** Dropdown row click pushes a new history entry (non-replace) or replaces (so browser back doesn't accumulate peek-trail)? Recommendation: non-replace (matches `usePeekState.openPeek` default — already non-replace). Same as canvas-click behaviour.
- **Q2.** When the dropdown is open and the user clicks elsewhere on the canvas, does the dropdown close? Recommendation: yes — outside-click dismisses (same UX as the filter popover already does).
- **Q3.** Should the search bar mount on first render or only after first focus? Recommendation: mount the input always (visible, ~40px tall, top-right); the dropdown mounts only when there are results.

If Toni wants pan-on-jump after PR 2 ships, that becomes a separate task (see §6.4) — not in scope here.

---

## 15. Implementation Guidance for the Next Agent

### 15.1 PR 1 — `/search` ByIdPanel (this PR, Sarah-as-implementer per #1165)

Build order:

1. **Design doc** at `docs/architecture/open-by-id.md` — this file. Done.
2. **`SearchPage.tsx` edits:**
   - Add `'by-id'` to the `Tab` union.
   - Add `{ id: 'by-id', label: 'By ID', Icon: Hash }` to `TABS`. Import `Hash` from `lucide-react`.
   - Add `ByIdPanel` function component below `PathPanel`, mirroring `LinkedPanel`'s structure (input, submit, validation).
   - Add `{activeTab === 'by-id' && <ByIdPanel />}` in the page body.
3. **Tests** at `frontend/src/features/nodes/SearchPage.test.tsx` — add a `describe('SearchPage — ByIdPanel')` block with ST1-ST4.
4. **Self-audit per §6.10 of the briefing** — comments grep + TSDoc ceiling check before claiming PR-ready.
5. **Open PR** with title and body as in §12.

### 15.2 PR 2 — workspace search bar (follow-up Pierre task)

Build order:

1. Add `WorkspaceSearchBar.tsx` with `classifyInput` exported and the component using `useNodeSemantic` (250 ms debounce).
2. Modify `WorkspacePage.tsx` to render `<WorkspaceSearchBar onOpenPeek={openPeek} />` as a sibling of `WorkspaceCanvas`.
3. **DO NOT** add new props to `WorkspaceCanvas`. The canvas stays untouched.
4. Tests per §13.2.
5. Open PR referencing this design + the previously-merged PR 1.

### 15.3 Specific anti-patterns to avoid

- **Do not** call `useNode` or `useNodeSemantic` inside `WorkspaceCanvas` to wire the search bar. The bar lives in `WorkspacePage` as a sibling.
- **Do not** add a new query-key shape. The semantic dropdown reuses the existing `['nodes', 'semantic', ...]` key.
- **Do not** add a feature flag, a config knob, an audit column, or a "for future" extensibility hook.
- **Do not** add pan-on-jump in PR 2. §6.4 is the decision.
- **Do not** bundle the modal-create-node fix (#1606) into either PR.
- **Do not** open a design-only PR. PR 1 ships design + the `/search` increment per #1165.

---

## 16. References

- DiVoid **#1604** — task.
- DiVoid **#293** — workspace search deferral (subsumed by PR 2).
- DiVoid **#283** — workspace mode design (must not regress).
- DiVoid **#1254** — peek modal architecture (foundation).
- DiVoid **#271** — render-stability binding constraint.
- DiVoid **#1136** — Design Contracts.
- DiVoid **#1165** — PR-shape rule.
- DiVoid **#1166** — architect-brief citation rule.
- DiVoid **#1184** — anti-complexity rule.
- DiVoid **#1267** — DRY block-level threshold.
- DiVoid **#420** — frontend code contracts.
- DiVoid **#275** — load-bearing test discipline.
- File at repo: `frontend/src/features/workspace/usePeekState.ts` (provides `openPeek`).
- File at repo: `frontend/src/features/workspace/WorkspaceNodePeekModal.tsx` (consumes `peekId`).
- File at repo: `frontend/src/features/nodes/useNodeSemantic.ts` (semantic search hook).
- File at repo: `frontend/src/features/nodes/SearchPage.tsx` (extends here in PR 1).
