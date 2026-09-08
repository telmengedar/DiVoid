# Architectural Document: NodeService.Patch — Access Gate on the LastUpdate Tail Write

**Source task:** DiVoid #6118 (**severity 4** — raised from 3 on this document's disclosure finding) · **File node:** #5951 · **Backend node:** #5861
**Status:** **IMPLEMENTED — QA approved with warnings, #13293.** Superseded in part by measurement; see the banner below. · **Author:** Sarah (architect) · **Date:** 2026-09-07, updated 2026-09-08 post-QA
**Repo copy:** `docs/architecture/nodeservice-patch-access-gate.md` — this node and that file are the same document; an edit to one is not finished until the other matches it (#11228 Lesson 2).
**Load-bearing standards:** Backend Code Contracts (#114), Design Contracts (#1136), retraction discipline (#11228 Lesson 3).

---

> ### SUPERSEDED-BY-MEASUREMENT — 2026-09-08
>
> This document was written before implementation. It shipped, QA ran it against mutations (#13293), and
> **three of its claims are now answered by measurement rather than by reading.** Per #11228 Lesson 3 the
> superseded text is **retracted in place, not deleted** — a reader who already acted on it must be able to
> see what changed and why.
>
> | Claim as written | Now | Where |
> |---|---|---|
> | "pending implementation" | Implemented; QA approved with warnings (#13293) | this banner |
> | Q1 — `Content` on the readback "unverified" | **ANSWERED: it is not encoded.** Metadata + `Substance` only | §2.3, §2.5, Q1 |
> | Q4 — severity "unchanged at 3" | **ANSWERED: raised to 4** by the orchestrator on the disclosure finding | header, Q4 |
> | §2.3 field set "at minimum `DefaultListFields`" | **Qualified** — M4b shows `GetNodeById` does *not* project via `DefaultListFields`; the enumeration was a ballpark, not the mechanism | §2.3 |
> | §7 criterion "sits behind a gate that throws" | **Sharpened** by QA to the structural form: the gate is *unconditional* vs. *inside an `if` an empty array skips* | §7 |
>
> **§11 is deliberately NOT rewritten.** Its "Not run: no reproduction of the live HTTP call was executed"
> is a record of what this document did and did not measure at the time it was written, and it is the reason
> the discrimination proof was carried into §8 step 4 as a deliverable instead of letting line-pinned reading
> pass as measurement. It stands as written, with a pointer to the results that closed it.
>
> **Two decisions were vindicated on evidence, not argument** (QA #13293): mutation **M3** — bare-id tail write
> *plus* `isAdmin: false` readback — leaves a status-only test **green** while tampering continues, proving the
> two halves are independent and that §8 step 3's `Assert.Multiple` (status *and* unchanged `LastUpdate`) was
> necessary rather than belt-and-braces. **M2/M3 together** confirm Decision 1's rejection of the
> conditional-write alternative: it closes neither half reliably.

---

## TL;DR

The defect **reproduces**. `NodeService.Patch` ends with an ungated `UPDATE … SET LastUpdate WHERE Id = @id` (`NodeService.cs:971-975`). On an empty patch array neither gated statement executes, nothing throws, and the method commits and returns `GetNodeById(…, isAdmin: true)` — so the caller also receives the node's full metadata. The report named the timestamp bump; the **information disclosure is the larger half and was not reported**.

Both stem from one missing gate, so one change fixes both: apply the already-composed `predicate` to that statement and throw `NotFoundException<Node>` on zero affected rows. Do **not** make the bump conditional on "something changed" — that contradicts settled precedent (#11405) and would not close the disclosure.

Recurrence sweep: the shape appears 7× in `NodeService.cs`; **only `Patch` is exploitable.**

---

## 1. Problem Statement

`PATCH /api/nodes/{nodeId}` must apply patch operations only to nodes the caller is permitted to write, and must be indistinguishable from "no such node" otherwise. Today one statement in the method escapes that rule.

**Success criteria:**

1. No caller can cause any observable state change on a node that fails the write gate.
2. No caller can obtain node metadata for a node that fails the gate, via this endpoint.
3. The endpoint's response for "not permitted" is byte-identical to "does not exist".
4. Legitimate patches — including an empty patch array by a permitted caller — keep their current behaviour.

---

## 2. Verification of the Reported Defect

The brief instructed me to treat #6118 as a claim. It is **true, and understated**.

### 2.1 The ungated statement

`Backend/Services/Nodes/NodeService.cs:971-975`:

```
971:        DateTime patchedAt = DateTime.UtcNow;
972:        await database.Update<Node>()
973:                      .Set(n => n.LastUpdate == patchedAt)
974:                      .Where(n => n.Id == nodeId)
975:                      .ExecuteAsync(transaction);
```

The method composes an access predicate at `:925-949` and applies it as `.Where(predicate.Content)` at `:955` (the main patch update) and `:965` (the `/type` branch). Line `:974` uses the **bare id** instead.

### 2.2 The path that reaches it with no gate at all

For `patches == []`:

| Line | Condition | Result |
|---|---|---|
| `:922-923` | `TouchesPath` for `/ownerId`, `/access` | both false |
| `:948` | `nameTouched` | false |
| `:951` | `remainingPatches.Length > 0` | **false** — gated update at `:955` skipped |
| `:960` | `typeOp != null` | **false** — gated update at `:965` and its `NotFound` check at `:967-968` skipped |
| `:972-975` | ungated `LastUpdate` write | **executes** |
| `:983` | `transaction.Commit()` | commits |
| `:984` | `return GetNodeById(nodeId, callerId, isAdmin: true)` | returns node details |

No `NotFoundException` can be thrown on this path. The ungated write is the **only** statement that runs.

### 2.3 The unreported half — information disclosure

`:984` passes a hard-coded `isAdmin: true`. `GetNodeById` (`:1247-1260`) builds its read gate via `NodeAuthorization.BuildVisibilityPredicate`, which returns `null` when `isAdmin` is true (`NodeAuthorization.cs:23`). So the readback is **not visibility-filtered**.

That is safe everywhere else, because every other path to `:984` has already executed a gated statement that throws on zero rows. The empty-patch path has not. A caller therefore receives `NodeDetails` for a node they cannot read.

**Exposure is precisely scoped.** It is a *marginal* disclosure only where `Access & Read == 0` and the caller is not the owner — otherwise `GET /api/nodes/{id}` would already return the row. Nodes in that state are real and routine: `NodeAccessHttpTests` creates them with `Access = NodeAccess.None` (`:317`, `:334`).

~~The projected fields are at minimum `NodeMapper.DefaultListFields` (`NodeMapper.cs:40`) — `id, type, name, status, severity, rootNodeId, contentType, ownerId, access, created, lastupdate` — plus `substance`~~ … ~~**Whether `Content` itself is also encoded on this path is unverified.**~~

> **RETRACTED IN PART 2026-09-08 — superseded by measurement (QA #13293).** Two corrections, one narrowing and one widening:
>
> 1. **The mechanism was wrong.** Mutation **M4b** appended `"content"` to `DefaultListFields` and the probe stayed **green** — so `GetNodeById` does **not** project through `DefaultListFields` at all. The enumeration above was a *ballpark of what comes back*, arrived at by reading; it was never a statement of the projection mechanism and should not have read as one. The actual field set handed to `FieldMapper.EntityFromOperation` remains a `Pooshit.AspNetCore.Services` internal not visible from this repo.
> 2. **`Content` is NOT disclosed — Q1 is answered.** The by-id readback returns metadata plus `Substance` only. John pinned it with a test; Jenny established the probe is not vacuous by deleting `PostProcess`'s early-return (`NodeMapper.cs:48-49`) and confirming it reddens.
>
> **What survives unchanged:** `substance` *is* returned on a by-id read with no `?fields=` opt-in (`GetById_ReturnsSubstanceInline`, `NodeSubstanceHttpTests.cs:172-186`), and it is a condensed rendering of the node's content — so the disclosure is content-*derived*, not merely structural. That is the load-bearing half of this paragraph and it held.
>
> **Net effect on severity:** bounded — the leak is metadata + `Substance`, not the content blob. **Net effect on the fix: none.** The gate closes the path regardless of what the readback projects.

### 2.4 Reachability through HTTP

`NodeController.cs:163-170` is `[HttpPatch("{nodeId:long}")] [Authorize(Policy = "write")]` and passes `[FromBody] PatchOperation[] patches` to the service with no empty-array guard. The controller carries `[ApiController]` (`:24`) and `Startup.cs` registers no `SuppressModelStateInvalidFilter`, so a body of `[]` binds to a valid empty array and reaches the service. A *missing* body is rejected as 400 by `[ApiController]` inference and is not the vector; `[]` is.

The `write` policy (`Startup.cs:214`) grants any principal holding the `write` permission — it is not node-scoped. Node-scoping is exactly what `NodeAuthorization` is for, and is what `:974` skips.

### 2.5 What is measured vs. read

| Claim | Basis |
|---|---|
| Control flow of `Patch`, `GetNodeById`, `NodeAuthorization`, `NodeController` | Source read, pinned to line above |
| `NodeAccessHttpTests` passes on the current tree | **Run.** 26/26 (command + output in §11) |
| The gate is exercised only with **non-empty** patch arrays | **Run.** `grep` for empty-array literals across `Backend.tests/` returned **zero hits** (§11) |
| `Content` presence on the readback | ~~**Not verified** — Q1~~ → **Measured 2026-09-08: not encoded.** Metadata + `Substance` only (QA #13293; M4b plus the `PostProcess` mutation) |

No test in the suite constructs an empty patch array. The defect is uncovered, not covered-and-broken.

---

## 3. Scope & Non-Scope

**In scope:** the access gate on `NodeService.Patch`'s `LastUpdate` write, the resulting `NotFound` behaviour, and regression coverage for the empty-patch path.

**Out of scope, explicitly:**

- Changing the `isAdmin: true` readback idiom. It is codebase-wide (`CreateNode:158`, `Patch:984`, `PatchContent:1221`, `MessageService.cs:59`) and correct under the invariant in §5.
- Rejecting empty patch arrays with 400 (see Decision 3).
- The `LinkNodes` / `UnlinkNodes` / `PatchLink` target-side gating gap (§7) — separate defect, separate PR.
- Any change to `NodeAuthorization`'s truth table.
- Embedding, substance, or content behaviour.

---

## 4. Assumptions & Constraints

| # | Assumption | Confidence |
|---|---|---|
| A1 | `NotFoundException<Node>` maps to HTTP 404 via the `Pooshit.AspNetCore.Services` middleware | High — used for this purpose throughout the file |
| A2 | An Ocelot `Update` returns the count of rows matched by the `WHERE` | High — `:955-957`, `:965-968` already depend on it |
| A3 | 404-for-forbidden is the deliberate convention, not an accident | **Verified** — `GetById_Stranger_Returns404OnPrivateNode`, `Patch_Stranger_Returns404OnPrivateNode`, `Delete_Stranger_Returns404OnPrivateNode` all assert 404 |
| A4 | No client depends on empty-patch-on-a-foreign-node returning 200 | Medium — see Q2 |

**Constraint (Code Contracts #114):** every caller-error guard in this file throws `NotSupportedException` to produce a 400. This fix must **not** use it — a failed access gate is a 404, not a caller error. `NotFoundException<Node>` is the correct type.

---

## 5. The Invariant This Codebase Was Missing

The durable output of this analysis is a rule the code relies on but never states:

> **Every path through a node-mutating service method must execute at least one access-gated statement that throws when it affects zero rows — before any ungated write, and before the `isAdmin: true` readback.**

`Patch` violates it on exactly one path. Naming it converts "remember to gate the tail write" into a checkable property, and it is the reason the fix is *one* change rather than two: restore the invariant and both the tamper and the disclosure close together.

---

## 6. Design Decisions

### Decision 1 — Gate the write. Do not make it conditional. (the core change)

The brief correctly flagged that #6118's proposed remedy bundles two different fixes. They are not equivalent.

| Option | Closes tamper? | Closes disclosure? | Produces 404? | Verdict |
|---|---|---|---|---|
| **(a) Gate the statement with `predicate`** | Yes | **Yes** | Yes | **Chosen** |
| (b) Skip the write when nothing changed | Yes | **No** — `:984` still runs | No | Rejected |
| (c) Both | Yes | Yes | Yes | Rejected — (b) adds nothing over (a) |

**Why (b) is rejected on its own terms, not just as redundant:**

1. **It does not close the disclosure.** Skipping the write leaves `:983-984` untouched; the caller still receives the node.
2. **It contradicts settled precedent.** #11405 decided *on measurement* that this file's bookkeeping writes are unconditional: `UploadContent` and `PatchContent` clear `Substance`, regenerate `Embedding`, and rewrite `LastUpdate` with no change-comparison, precisely so a row cannot hold "three disagreeing answers to *did content change?*". A conditional `LastUpdate` in `Patch` would make it the file's sole exception and re-open that inconsistency.
3. **"A real change occurred" is not cleanly expressible.** A SQL `UPDATE` reports a matched row whether or not any value differs, so for every non-empty patch (b) degenerates to (a). The only case where the two differ is the empty array — which (a) already handles correctly.

**The change:** replace `.Where(n => n.Id == nodeId)` at `:974` with the already-composed `.Where(predicate.Content)`, capture the affected count, and throw `NotFoundException<Node>(nodeId)` when it is zero.

`predicate` is already in scope and already carries `n.Id == nodeId` ANDed with the gate (`:941-943`), so this introduces no new expression and no new helper.

**Re-evaluating the gate after the main update is safe.** The only patch operations that could move a row out of its own gate are `/ownerId` and `/access`. `/ownerId` requires admin, whose gate is `null` (`:926-931`). `/access` is gated by `BuildOwnerPredicate` (`:934`), which matches on `OwnerId` — a field that branch cannot change. So no legitimate patch can make the tail gate fail after the main gate passed.

### Decision 2 — 404, and do not distinguish it from "no such node"

The endpoint returns `404` with the same body for "node does not exist" and "you may not write it". This is the existing, deliberate convention (A3) and it is what prevents the endpoint from being an existence oracle — which is the very property under attack here. **Keep it. Change nothing about the response shape.**

Concretely: the new guard throws `NotFoundException<Node>(nodeId)`, identical to `:957` and `:968`. No new status code, no new error payload, no distinguishing message.

### Decision 3 — An empty patch by a *permitted* caller stays a 200 that bumps `LastUpdate`

This is the case where gating and skipping visibly diverge, so it needs an explicit call.

**Chosen: 200, `LastUpdate` bumped.** After the fix the gated statement matches the caller's own row, affects 1, and the method proceeds exactly as today.

Rationale: it is the current behaviour and no consumer has complained; it is consistent with the unconditional-bookkeeping precedent above; and an empty patch array is a well-formed request, not a caller error. Turning it into a 400 would add a new `NotSupportedException` guard and a new rejection surface to fix a security defect that does not require it — scope creep across a behaviour change to a public endpoint. If a "touch" primitive is later judged undesirable, that is its own task with its own consumer review.

---

## 7. Recurrence Audit — does this shape occur elsewhere?

The brief asked whether "gated main update, ungated bookkeeping write after it" recurs. It does — and the distinction that matters is not the shape but its **reachability**.

Swept: all 20 files under `Backend/Services/`, plus a repo-wide grep for `Update<|Delete<|Insert<` over `Backend/**/*.cs` (excluding `obj/`), which found exactly one write site outside `Services/` (`Init/DatabaseModelService.cs:41`, a startup migration with no caller).

**The shape occurs 7×, all in `NodeService.cs`. Exploitable: 1.**

| # | Method | Ungated write | Gate is a hard precondition? |
|---|---|---|---|
| 1 | **`Patch`** | `:974` | **No — skippable on empty patch** |
| 2 | `PatchContent` | `:1215`, `RegenerateContentEmbedding:1182/:1187` | Yes — gated load `:1203` throws on null `:1205` |
| 3 | `UploadContent` | `RegenerateContentEmbedding:1182/:1187` | Yes — gated update `:1146` throws on 0 `:1148` |
| 4 | `Delete` | `:195` link cascade | Yes — gated delete `:192` throws on 0 `:194` |
| 5 | `LinkNodes` | `:238-241`, `:243` | Source yes `:230-231`; **target never gated** — see below |
| 6 | `UnlinkNodes` | `:1099-1101` | Source yes `:1096-1097`; target not gated |
| 7 | `PatchLink` | `:1120-1123` | Source yes `:1114-1115`; target not gated |

In rows 2-4 the ungated statement is unreachable without first passing a gate that throws, so the shape is a **style** liability, not a security one.

> **SHARPENED 2026-09-08 by QA (#13293), which spot-checked three of the six other sites.** "Sits behind a gate that throws" states the symptom; the structural criterion is crisper, and it is what an implementer or reviewer should actually check:
>
> > **In `Delete`, `UploadContent` and `PatchContent` the gate is *unconditional* — it executes on every call. In `Patch` both gates sit inside `if` blocks (`:951`, `:960`) that an empty array skips.**
> >
> > The distinguishing property is therefore not *"is there a gate downstream"* but ***"can any input reach the tail write without executing the gate at all"***.
>
> **`PatchContent` is the closest analogue and the sharpest illustration:** it is *also* array-driven (`ContentEdit[] edits`), so the same empty-array input is available to it — and it is safe purely because its gated load at `:1203` is **not** inside a length check. Same input shape, opposite outcome, one structural difference. That, rather than the mere presence of a gate, is the property to preserve when this file is next edited.

`RegenerateEmbeddingViaBranches` (`:1010-1026`, all four branches bare-id at `:1057/:1066/:1074/:1081`) is likewise reached only at `:980` under `if (nameTouched)`, which requires a non-empty `/name` operation and therefore guarantees the gated update at `:955` ran. (Original text — restored outside the quoted QA criterion above, which it had been spliced into.)

**This bounds the fix to `Patch`.** No other method needs to change in this PR.

### A separate, unreported finding (verified, NOT in this PR's scope)

Rows 5-7 gate the **source node only**. I verified `LinkNodes` myself at `NodeService.cs:220-245`: the gate at `:225-231` covers `sourceNodeId`; `:234` then checks the *target* for bare existence only, and `:243` calls `TryAnchorOrphanToPositioned(transaction, targetNodeId, [sourceNodeId])`, whose write at `:78-81` is `.Where(n => n.Id == candidateId)` on the target. A caller with write access to their own node can therefore (a) attach a link to any node by id, including one they cannot read, and (b) rewrite that node's `X`/`Y` when it is unpositioned (`:61-62` limits the write to nodes at `0,0`).

This is a genuine defect, distinct in cause and remedy from #6118. **It must be filed and fixed separately** — bundling it here would violate the one-feature-one-PR rule. See Q3.

---

## 8. Implementation Guidance

> **IMPLEMENTED 2026-09-08 — QA approved with warnings (#13293).** Steps 1-5 are done and measured; step 4's discrimination proof was run and reddened as required. Step 6 (file node #5951) is the orchestrator's post-merge graph write, not the implementer's. Retained as written, as the record of what was specified versus what shipped.

Ordered; one PR, one unit of work.

1. **Gate the statement.** In `NodeService.Patch`, change `:974` from `.Where(n => n.Id == nodeId)` to `.Where(predicate.Content)`; assign `ExecuteAsync(transaction)` to a local count and throw `NotFoundException<Node>(nodeId)` when it is `0`. Use `NotFoundException<Node>`, **not** `NotSupportedException` (§4 constraint).

2. **Leave the existing guards at `:955-957` and `:965-968` in place.** They are now strictly redundant — same predicate, same row, therefore the same affected-count — but they fail fast *before* `ResolveOrCreateTypeId` at `:962` does speculative work, and deleting them changes failure ordering for no observable gain. DRY math per Design Contracts §1: `block_size × site_count = 2 × 3 = 6`, far below the ~15-20 threshold. Inline guards are correct here; **do not refactor them into a helper.**

3. **Add the discriminating regression test** to `Backend.tests/Tests/NodeAccessHttpTests.cs`, alongside `Patch_Stranger_Returns404OnPrivateNode`:
   - a stranger `PATCH`ing `[]` against a node with `Access = NodeAccess.None` gets **404**;
   - the node's `LastUpdate` is **unchanged** afterwards (read it back as the owner and compare) — the status assertion alone would pass even if the write still fired before the throw;
   - the owner `PATCH`ing `[]` against their own node still gets **200** (pins Decision 3).

4. **Prove the test discriminates.** Revert `:974` to the bare-id form and confirm the new test **reddens**. A test that passes against the unfixed code pins nothing. Report the red output in the PR.

5. **Re-run** `dotnet test Backend.tests/Backend.tests.csproj --filter "FullyQualifiedName~NodeAccessHttpTests"` and confirm 26 → 29 passing (or state the actual count).

6. **Update file node #5951.** Its "Watch out for" bullet describes this defect as live. Per #11228 Lesson 3, do **not** delete the bullet — mark it in place with the fix date, the PR, and the fact that falsified it. The node and this document are two copies of the same claim; an edit to one is not finished until the other matches.

---

## 9. Risks & Mitigations

| Risk | Likelihood | Mitigation |
|---|---|---|
| A client relies on empty-patch-as-touch against a node it does not own | Low | That is the vulnerability; breaking it is the fix. Q2 asks for confirmation before merge. |
| The main gate passes but the tail gate fails, producing a spurious 404 after partial work | Very low | Argued impossible in Decision 1; the transaction is not committed until `:983`, so a throw rolls back cleanly. |
| The new test passes vacuously | Medium | Step 4 mandates the mutation check. |
| Fix is read as covering rows 5-7 of §7 | Medium | §3 and §7 state the boundary explicitly; Q3 asks for separate tasks. |

**Failure mode check:** the new throw occurs inside the `using Transaction` scope opened at `:949` and before `Commit()` at `:983`, so no partial state escapes.

---

## 10. Design Contracts (#1136) §5 Pre-Design Checklist

| Item | Status |
|---|---|
| No new type mirroring an existing one | Pass — no new types |
| No new abstraction with one implementation | Pass — reuses in-scope `predicate` |
| No element justified by "we might need X later" | Pass |
| No deprecation period / feature flag / shim | Pass — single atomic change |
| `block_size × site_count` quoted for every inline decision | Pass — §8 step 2, `2 × 3 = 6` |
| Existing service/DTO audited before adding a layer | Pass — `NodeAuthorization` already owns this concern; no new layer |
| No new persisted data | Pass |
| No new config knob | Pass |
| Can-it-be-deleted / merged / inlined applied | Pass — the fix *removes* a special case rather than adding one |
| Trade-offs named explicitly | Pass — Decisions 1-3 |
| Out-of-scope items listed explicitly | Pass — §3 |
| Cites #114 and #1136 as load-bearing | Pass — header, §4, §8 |
| Reader/scope inventory explicit | Pass — §7, full sweep with denominator |
| No defensive code for impossible scenarios | Pass — Decision 1 argues the post-update gate flip is impossible and does **not** add a guard for it |
| No superseded predecessor doc left live | N/A — no predecessor |

---

## 11. Commands Run, With Output

**1. Baseline test run** — establishes the suite is green before any change:

```
$ dotnet test Backend.tests/Backend.tests.csproj --filter "FullyQualifiedName~NodeAccessHttpTests"
  Backend -> C:\dev\claude\divoid\Backend\bin\Debug\net9.0\Backend.dll
  Backend.tests -> C:\dev\claude\divoid\Backend.tests\bin\Debug\net9.0\Backend.tests.dll
Bestanden!  : Fehler: 0, erfolgreich: 26, übersprungen: 0, gesamt: 26, Dauer: 8 s
```

**2. Empty-patch coverage probe** — establishes the defect is uncovered:

```
$ grep -rn "Array.Empty<PatchOperation>\|new PatchOperation\[0\]\|new PatchOperation\[\] { }" Backend.tests/
(no output — zero hits)
```

**3. 404-convention probe** — establishes A3:

```
$ grep -n "public.*Task" Backend.tests/Tests/NodeAccessHttpTests.cs
...
228:  GetById_Stranger_Returns404OnPrivateNode
327:  Patch_Stranger_Returns404OnPrivateNode
509:  Delete_Stranger_Returns404OnPrivateNode
```

**4. Global-filter probe** — establishes the empty array is not rejected before the service:

```
$ grep -n "SuppressModelStateInvalidFilter\|AddControllers\|Filters.Add" Backend/Startup.cs
98:        services.AddControllers(o =>
(no SuppressModelStateInvalidFilter, no global filters)
```

**5. Recurrence sweep** — all 20 files under `Backend/Services/` read or grep-verified write-free; repo-wide `Update<|Delete<|Insert<` over `Backend/**/*.cs` excluding `obj/` found one write site outside `Services/` (`Init/DatabaseModelService.cs:41`). Result tabulated in §7. `NodeAuthorization` is referenced from exactly 9 sites, all in `NodeService.cs`: `:185, :202, :225, :934, :938, :1091, :1109, :1136, :1195, :1252`.

**Not run:** no reproduction of the live HTTP call was executed, because the design-only boundary excludes adding the test that would drive it. §2 rests on source reading pinned to line, plus the four probes above. Step 4 of §8 converts it to a measured result at implementation time.

> **STANDS AS WRITTEN — closed 2026-09-08, deliberately not rewritten.** §8 step 4 was executed and the discrimination proof passed; QA (#13293) ran three further mutations beyond the reported set. The paragraph above is **kept verbatim** because it records what this document had and had not measured at the time it was written. It is the reason the proof was carried into §8 as a deliverable instead of letting line-pinned reading pass as measurement — and mutation **M3** shows that mattered: a status-only assertion is **green** against a half-fix (bare-id tail write *plus* `isAdmin: false` readback) while tampering continues. Rewriting this paragraph to claim the reproduction as done would destroy the only evidence that the gap was known and closed downstream on purpose, rather than overlooked.

---

## 12. Open Questions

**Q1 — Does the `Patch` readback include `Content`? — ANSWERED 2026-09-08: NO.**

~~`GetNodeById:1249` calls `mapper.CreateOperation(database)` with no field list, so the field set handed to `FieldMapper.EntityFromOperation` (a `Pooshit.AspNetCore.Services` internal) is not visible from this repo. `NodeMapper.PostProcess` (`:47-52`) encodes `Content` only when the field set contains `"content"`. Every test asserting `Content` presence targets the *list* endpoint (`NodeListInlineContentHttpTests`, `NodeSubstanceHttpTests:227`); none covers by-id.~~

The probe was run. **The by-id readback does not encode `Content`** — it returns metadata plus `Substance` only. John pinned it with a test; Jenny established the probe is not vacuous by deleting `PostProcess`'s early-return and confirming it reddens. A second mutation (**M4b**) appending `"content"` to `DefaultListFields` left the probe green, which additionally shows `GetNodeById` does not project via `DefaultListFields` — see the retraction in §2.3.

**As predicted, this changed the disclosure's severity and not the fix.**

~~Probe: `GET /api/nodes/{id}` on a node with content and check whether the `content` key is present.~~ — **WITHDRAWN 2026-09-08:** this probe was run and is answered above. Left visible rather than deleted so a reader who queued it can see it closed.

**Q2 — Is any client using an empty patch as a "touch"?** The fix makes it 404 for non-permitted callers. Permitted callers are unaffected (Decision 3). Worth one grep of `divoid-mcp/` and `frontend/` before merge — I did not sweep them, as the brief scoped me to `Backend/`.

**Q3 — Shall I file the §7 `LinkNodes` / `UnlinkNodes` / `PatchLink` target-side gap as its own DiVoid task?** I verified `LinkNodes` directly and did not file, to stay inside the design-only boundary. It should not ride this PR.

**Q4 — Should `severity` on #6118 be raised from 3? — ANSWERED 2026-09-08: YES, raised to 4.**

~~The disclosure half was not part of the original assessment. I have not changed the field.~~

The orchestrator raised #6118 to **severity 4** on this document's disclosure finding. The header of this document now reads severity 4.
