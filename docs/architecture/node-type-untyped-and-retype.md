# Architectural Document: Node Type Model — Untyped Create & Retype

> Load-bearing references: **Design Contracts** (DiVoid #1136 — §1 KISS/DRY/YAGNI, §2 existing-systems-first, §4 less-is-better) and **Code Contracts** (DiVoid #114 §0 backend, §420 frontend). This design was written against those rules and the §5 Pre-Design Checklist; a violation cites the section number.
>
> Source tasks: DiVoid **#2011** (untyped create) and **#2012** (retype). Both surfaces are governed by ONE architectural concern — how a `Node` references its type via the separate `NodeType` table — so they share one design document.

## 1. Problem Statement

DiVoid stores a node's type as a foreign key (`Node.TypeId : long`) into a separate `NodeType` table; the API speaks type *names* (`NodeDetails.Type : string`) resolved against that table. Two user-facing gaps fall out of this indirection:

1. **Untyped create.** A node can already exist with no type — the backend resolves `node.Type == null` to a null-named `NodeType` row, and the `notype=true` list filter (DiVoid #1975) reads exactly those rows. But the path is messy: there is no normalization of empty-string `""` versus `null`, so two callers can spawn two different "untyped" representations. The UI also forces type to be a required field on the create form.

2. **Retype.** Changing a node's type is not supported today, deliberately: `Node.TypeId` is not `[AllowPatch]`, and a test asserts `PATCH /typeid → 400`. Because the API speaks names and the entity stores ids, a retype is a name→id resolve-or-create (the same logic `CreateNode` already has), not a plain property patch.

**Goal:** settle the canonical node↔type representation once, then expose both surfaces consistently on it.

**Success criteria:**
- A node can be created with no type through a clean, normalized path; the shipped `notype` filter keeps finding it.
- A node's type can be changed to any other type (including to/from untyped) by name, resolving-or-creating the `NodeType` exactly as create does.
- The frontend can warn (non-blocking) when a retype is semantically odd, without the backend ever blocking it.

## 2. Scope & Non-Scope

**In scope (this design doc):**
- The canonical untyped representation and the `""`-vs-`null` normalization rule.
- The retype API shape and resolution semantics.
- The frontend soft-warning heuristic (design only — Pierre implements later).

**In scope (THIS PR — bundled backend increment, branch `feat/untyped-nodes`):**
- Feature 1 backend only: normalize `""` → untyped on create. Tests for untyped create + normalization.

**Out of scope (named follow-up PRs):**
- **PR-B — retype backend.** The `PATCH /type` special-case in `NodeService.Patch`. Not implemented here.
- **PR-C — frontend untyped create.** Make the type field optional on the create form; render untyped nodes.
- **PR-D — frontend retype control + soft warning.** The type-edit control and the warning heuristic from §9.
- Migration/canonicalization of the ~13 existing null-named `NodeType` rows (DiVoid #508) — **explicitly not done**; §7 explains why they need no migration.
- Any change to embedding behaviour — embeddings are name+content based and independent of type (confirmed: `RegenerateEmbeddingViaBranches` and `CreateNode`'s embedding step never read `TypeId`).
- Any new persisted column, sentinel `TypeId` value, or `NodeType` schema change. None is needed.

## 3. Assumptions & Constraints

| # | Assumption / Constraint | Confidence |
|---|---|---|
| A1 | Untyped is already a first-class read concept: `notype=true` queries `NodeType.Type == null` (NodeService.cs:325-340). Any representation change must keep that filter working. | Verified in code |
| A2 | `CreateNode` resolves `NodeType WHERE Type == node.Type` and creates-if-missing (NodeService.cs:88-100). With `node.Type == null` it resolves/creates a null-named row. With `node.Type == ""` it would resolve/create a *distinct* empty-string-named row. | Verified in code |
| A3 | The ~13 existing null-named `NodeType` rows are the correct untyped representation, not corruption (DiVoid #508). | Verified (finding #508) |
| A4 | `Node.TypeId` is deliberately not `[AllowPatch]`; `PATCH /typeid → 400` is asserted by `Patch_NonAllowPatchedProperty_TypeId_Returns400`. | Verified in test |
| A5 | The user has decided retype is fully permissive at the backend; semantic oddness is a frontend-only soft warning. | User quote, #2012 |
| A6 | Private monorepo, atomic deploy — no deprecation window / compat shim needed (Design Contracts §4). | Project convention |

## 4. Architectural Overview

No new components, tables, columns, or services. The entire change lives in the **service layer's existing name→id resolution seam**, which is the single point that already translates between the API's name vocabulary and the entity's id storage.

```
            POST /api/nodes                 PATCH /api/nodes/{id}  (body: [{op:replace, path:/type, value:"bug"}])
                  │                                   │
          NodeController.CreateNode           NodeController.Patch
                  │                                   │
          NodeService.CreateNode             NodeService.Patch
                  │                                   │
        ┌─────────┴─────────┐                ┌────────┴─────────┐
        │ normalize "" → null│                │ detect /type op  │  ← PR-B (not this PR)
        │ (THIS PR)          │                │ resolve name→id  │
        └─────────┬─────────┘                │ set TypeId in txn│
                  │                          └────────┬─────────┘
                  ▼                                   ▼
     ResolveOrCreateTypeId(name, txn)  ◄── shared seam, extracted from CreateNode's inline block
                  │
                  ▼
        NodeType table  (Type == null  ⇒  "untyped";  notype filter reads these rows)
```

**The canonical model (Decision 1):** untyped = a `NodeType` row whose `Type` is `null`. This is the model that already exists and that the shipped `notype` filter already reads. We do not introduce a `TypeId=0` sentinel, a dedicated singleton untyped row, or a schema change. The only correctness gap is that `""` can diverge from `null`; we close it by **normalizing `""` → `null` at the service boundary** (Decision 2).

## 5. Components & Responsibilities

| Component | Owns | Does NOT own |
|---|---|---|
| `NodeService.CreateNode` | Normalizing the inbound type marker (`"" → null`); resolving-or-creating the `NodeType`; inserting the node. | Deciding semantic validity of a type (there is none to decide). |
| `NodeService.Patch` (PR-B) | Detecting a `/type` patch op, resolving-or-creating the target `NodeType`, and setting `Node.TypeId` inside the existing patch transaction. | Blocking any transition; warning about oddness (frontend's job). |
| `ResolveOrCreateTypeId` (new private helper, this PR) | The single name→id resolve-or-create step (normalize → `WHERE Type == name` → insert-if-missing → return id), bound to a supplied transaction. | Anything beyond type resolution. |
| `notype` filter (`GenerateFilter`) | Reading untyped nodes via `NodeType.Type == null`. Unchanged. | — |
| Frontend create form (PR-C) | Allowing submit with no type. | — |
| Frontend retype control (PR-D) | The type-edit control + the soft warning from §9. | Enforcing the warning (it is advisory only). |

### DRY note — the resolve-or-create helper

`CreateNode` (NodeService.cs:88-100) and the future retype path (PR-B) both need the identical name→id resolve-or-create step. That is the same logic at two sites. Per Design Contracts §1 (block-level DRY, #1267): the block is ~10 lines and will live at 2 sites = ~20 lines of would-be duplication, and it has a clean 3-word name (`ResolveOrCreateTypeId`). It earns extraction. **This PR extracts it** (CreateNode calls it; the normalization lives inside it), so PR-B reuses it with zero duplication. Extracting now — while writing the create path — is cheaper than extracting in PR-B and re-touching CreateNode then.

## 6. Interactions & Data Flow

### 6.1 Untyped create (THIS PR)
1. `POST /api/nodes` with `Type` absent, `null`, or `""`.
2. `CreateNode` → `ResolveOrCreateTypeId(node.Type, txn)`.
3. Helper normalizes `""` → `null` (and trims-to-null any pure-whitespace string, mirroring the embedding layer's "name absent" convention so the type vocabulary stays clean).
4. `WHERE Type IS NULL` → reuses the existing null-named row if one exists, else inserts one. Returns its id. **Implementation note:** a captured-null variable compiles to `Type = NULL` (never true in SQL), which is the latent bug that let the ~13 stray rows accumulate (#508 — the old `CreateNode` used `Where(t => t.Type == node.Type)` with a captured value). The helper branches on the untyped case to emit a literal `== null` → `IS NULL`, so sequential untyped creates now consolidate onto one row instead of multiplying.
5. Node inserted with that `TypeId`. Round-trips as untyped; `notype=true` finds it.

### 6.2 Typed create (unchanged behaviour, now via the helper)
Same flow; `node.Type` is a non-empty name → resolve-or-create the named row. Behaviour identical to today.

### 6.3 Retype (PR-B — design only)
1. `PATCH /api/nodes/{id}` body contains `{op:"replace", path:"/type", value:"<name-or-null>"}`.
2. `Patch` detects the `/type` op *before* handing the remaining ops to the generic `[AllowPatch]` machinery (which would 400 on `/type` because it maps to `TypeId`, which is intentionally not patchable).
3. Inside the existing patch transaction, resolve target via `ResolveOrCreateTypeId(value, txn)`, then `UPDATE Node SET TypeId = <id> WHERE Id == nodeId` under the same visibility gate the rest of `Patch` uses.
4. Any other ops in the same patch array continue through the existing path. `LastUpdate` bumps as today.
5. No embedding regeneration (type is not an embedding input).

## 7. Data Model (Conceptual)

Unchanged. `Node ──TypeId──▶ NodeType`. Untyped = `NodeType.Type IS NULL`.

**The ~13 stray null-named rows (DiVoid #508): no migration.** They are already the canonical untyped representation. Whether one null-named row or thirteen exist is invisible to every consumer:
- `notype` filter uses `TypeId IN (SELECT Id FROM NodeType WHERE Type IS NULL)` — matches all of them equally.
- `CreateNode`/`ResolveOrCreateTypeId` does `WHERE Type == null` + `ExecuteEntityAsync` (first match) — reuses one, never multiplies on `""` once normalized.
- `ListTypes` groups by `(Id, Type)`; all null-named rows already collapse to untyped in the frontend (#508 maps absent-type → one `UNTYPED_VALUE` checkbox).

Collapsing them to a single row would be a destructive migration that buys nothing — a §6 "verification-SELECT-before-destructive-statement" exercise with zero consumer benefit. Per Design Contracts §4, the radical-clean choice here is *leave them*; the normalization rule (Decision 2) prevents future growth, which is the only real risk #2011 names.

## 8. Contracts & Interfaces (Abstract)

### 8.1 `ResolveOrCreateTypeId` (private service helper, THIS PR)
| Aspect | Semantics |
|---|---|
| Input | A type name (`string`, may be `null`/`""`/whitespace) and the open transaction. |
| Normalization | `null`, `""`, or pure-whitespace → `null` (the untyped marker). |
| Behaviour | `WHERE Type == <normalized>` → return existing id if found, else insert a row with that normalized value and return the new id. |
| Output | `long` type id, never 0-as-sentinel. |
| Invariant | A normalized-`null` input never creates a second untyped row when one already exists; `""` and `null` are indistinguishable downstream. |
| Transaction | All reads/writes bound to the supplied transaction (atomic with the node insert / type patch). |

### 8.2 Untyped create — observable contract (THIS PR)
| Input `Type` | Result |
|---|---|
| absent / `null` | Node created untyped; `notype=true` returns it; GET shows `Type` absent/null. |
| `""` | **Same as null** (normalized). No empty-string-named `NodeType` row is ever created. |
| whitespace-only | Same as null (normalized). |
| non-empty name | Typed as today. |

### 8.3 Retype — observable contract (PR-B)
| Input | Result |
|---|---|
| `PATCH /type` to existing type name | `Node.TypeId` repointed; node renders as that type. 200. |
| `PATCH /type` to new (unseen) name | `NodeType` row created; node repointed. 200. |
| `PATCH /type` to `null`/`""` | Node becomes untyped (resolves the null-named row). 200. |
| `PATCH /typeid` (the raw id path) | Still 400 — unchanged; the test `Patch_NonAllowPatchedProperty_TypeId_Returns400` continues to pass. Retype goes through `/type` (names), never `/typeid`. |

## 9. Cross-Cutting Concerns

- **Security / authorization.** Retype (PR-B) reuses the existing `Patch` visibility gate (`BuildVisibilityPredicate(..., write:true)`); no new boundary. Untyped create reuses the `write` policy on `POST /api/nodes`.
- **Concurrency / atomicity.** Resolve-or-create runs inside the same transaction as the node insert (create) or the type UPDATE (retype). Sequential untyped creates consolidate onto one null-named row (the `IS NULL` lookup reuses it). A benign race where two *concurrent* transactions both miss the lookup and each insert a null-named row is harmless — both render as untyped and `notype` matches both (§7). No locking needed (designing a lock here would be a §6 "defensive code for impossible-harm" anti-pattern).
- **Idempotency.** Retype to the current type is a no-op-equivalent UPDATE; safe to repeat.
- **Embeddings.** Untouched by either surface — type is not an embedding input.
- **Error handling.** Existing service-exception → HTTP middleware applies; no new exception types.

### 9.1 Frontend soft-warning heuristic (PR-D — design only, for Pierre)

Per the user: *"everything is allowed but warn when semantics might make no sense … you don't really lose info, but it doesn't necessarily make sense to have a high prio closed documentation."* The warning is **non-blocking, advisory, frontend-only**. The backend never sees or enforces it.

**Heuristic (deliberately simple — no per-type capability matrix; see KISS note below):**

> Show a non-blocking warning before confirming a retype **when both** of these hold:
> 1. The node currently carries **lifecycle state** — `status` is set (non-empty) **and/or** `severity` is set (non-null), **and**
> 2. The **target type** is one that does not conventionally carry lifecycle.

For condition 2, the frontend already knows the type vocabulary (it renders the type filter). Define a small **lifecycle-bearing type allowlist** in the frontend — the types for which status/severity are conventional: `task`, `bug` (and `untyped`, which carries nothing conventionally and so should never trigger a warning *as a target*, because losing the convention-fit is exactly what "untyped" means). Any target type **outside** that allowlist, when the node has lifecycle state, fires the warning. Concretely: retyping a node that has `severity=high, status=closed` into `documentation` warns ("This node has a status/severity that `documentation` nodes don't usually carry — keep anyway?"); retyping the same node into `bug` does not warn.

**Warning copy intent (not final wording):** name the carried lifecycle fields and the target type; offer "Change anyway" (default-able) and "Cancel". The fields are not cleared — the user keeps the data (matching "you don't really lose info").

**KISS justification (Design Contracts §1/§4).** The simplest correct form is a single allowlist of lifecycle-bearing types, not a per-type × per-field capability matrix. The matrix would be configurability with no operator (§3) and a mirror of knowledge the type system doesn't actually encode. The allowlist is 2-3 names in frontend code; if the convention shifts, it is a one-line edit. No backend involvement, no config knob. The allowlist lives in the frontend type module alongside the existing `UNTYPED_VALUE` constant (#508) — same home, same pattern.

**Pierre's open input (not a blocker for this design):** confirm the lifecycle-bearing allowlist membership with the user at implementation time if it extends beyond `task`/`bug`. The heuristic shape is fixed; only the allowlist contents are tunable, and they are a code constant per §3.

## 10. Quality Attributes & Trade-offs

| Attribute | How addressed |
|---|---|
| Simplicity | Zero new tables/columns/services/sentinels. One normalization rule + one helper extraction this PR; one patch special-case in PR-B. |
| Maintainability | Single name→id seam (`ResolveOrCreateTypeId`) used by both create and retype — one place to reason about type resolution. |
| Compatibility | `notype` filter, `ListTypes`, `/typeid → 400` test all unchanged and still correct. |
| Performance | No extra round-trips; resolve-or-create is the same one-or-two queries already in `CreateNode`. |

**Trade-off — keep `null` model vs. introduce a sentinel/singleton untyped row.**
- *Alternative considered:* a single canonical untyped `NodeType` (id pinned), or `TypeId = 0` with no join row.
- *Rejected because:* both are larger changes that break or complicate the shipped `notype` filter (which keys on `Type IS NULL`, not on a fixed id), and a `TypeId=0`-no-row model would break `NodeMapper`'s inner `Join<NodeType>` (untyped nodes would vanish from every list). The `null`-named-row model is the one the system already runs on; normalization is the minimal fix that closes the only real gap (`""` divergence). Cost of the simple choice: multiple null-named rows can coexist — but §7 shows this is invisible to all consumers, so the cost is zero in practice.

**Trade-off — `PATCH /type` special-case vs. dedicated `PUT /type` endpoint (PR-B).**
- *Chosen:* special-case `/type` inside `Patch`.
- *Why:* `Patch` already special-cases paths (`/ownerId`, `/access` have bespoke gate handling; `/name` triggers embedding regen). A `/type` branch is idiomatic there and lets a single patch array change type alongside other fields atomically. A dedicated endpoint would duplicate the gate/transaction scaffolding for no behavioural gain (Design Contracts §2 — no new layer without a concrete concern the existing surface can't serve).

## 11. Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Empty-string type rows already exist in prod and break the `notype` filter today. | Out of scope to migrate; the finding (#508) reports only *null*-named rows, no empty-string rows. Normalization prevents new ones. If empty-string rows are later found, a one-off cleanup task can `UPDATE NodeType SET Type=NULL WHERE Type=''` — filed only if observed, not speculatively (§3/YAGNI). |
| Retype to a brand-new typo'd name silently creates a junk `NodeType`. | Same behaviour as create today; acceptable. The frontend retype control (PR-D) should offer the existing type list as suggestions, not free-text-first — noted for Pierre, not a backend concern. |
| Frontend warning allowlist drifts from real conventions. | It is a code constant, trivially editable; not load-bearing for correctness (advisory only). |

## 12. Migration / Rollout Strategy

Atomic deploy; no migration. The four PRs ship in dependency order:
1. **This PR (`feat/untyped-nodes`)** — backend untyped-create normalization + helper extraction + tests. Includes this design doc.
2. **PR-B** — retype backend (`PATCH /type` special-case), reusing `ResolveOrCreateTypeId`.
3. **PR-C** — frontend: optional type on create form.
4. **PR-D** — frontend: retype control + §9.1 soft warning.

PR-C and PR-D depend on the respective backend PRs and reference them in their bodies.

## 13. Open Questions

- None blocking. The lifecycle-bearing-type allowlist membership (§9.1) is confirmable by Pierre with the user at PR-D time; the heuristic shape is fixed and needs no further input.

## 14. Implementation Guidance for the Next Agent

**This PR (backend, feature 1):**
1. Extract `ResolveOrCreateTypeId(string type, Transaction txn) → Task<long>` as a private method on `NodeService`, containing: normalize `null`/`""`/whitespace → `null`; `Load<NodeType> WHERE Type == normalized` (first match); insert-if-missing returning the id. Bind every op to `txn`.
2. Replace the inline resolve-or-create block in `CreateNode` (NodeService.cs:88-100) with a call to the helper, passing `node.Type` and the existing transaction. Behaviour for typed create must be byte-identical.
3. Tests (100% of new/changed production lines):
   - Create with `Type=""` → node is untyped; reuses/creates the *same* null-named row as `Type=null` (assert no empty-string `NodeType` row exists; assert `notype=true` returns the node).
   - Create with `Type=null`/absent → untyped (extend existing coverage if a gap).
   - Create with whitespace-only `Type` → untyped, no whitespace-named row.
   - Two untyped creates → both untyped, both found by `notype=true` (multiplicity is harmless).
   - Regression: typed create still resolves/reuses the named row (existing `CreateNode_ExistingType_ReusesNodeType` must stay green).
4. Run `dotnet test Backend.tests/Backend.tests.csproj`; report.

**PR-B (retype backend) — not this PR:** add the `/type` detection + resolve-via-helper + `TypeId` UPDATE branch in `Patch`; keep `/typeid → 400`. Tests for every row of §8.3.

**PR-C / PR-D (frontend) — not this PR:** per §2 and §9.1.

## What does NOT go in

- No new table, column, sentinel `TypeId`, or singleton untyped row (§4, §7).
- No migration of the existing null-named rows (§7).
- No backend validation/blocking of retype transitions (§9 — user decision).
- No per-type capability matrix or config knob for the warning (§9.1 KISS).
- No embedding changes (type is not an embedding input).
- No deprecation window / compat shim (atomic deploy, §3-A6).
- No dedicated retype endpoint (§10 — `Patch` special-case is idiomatic).
