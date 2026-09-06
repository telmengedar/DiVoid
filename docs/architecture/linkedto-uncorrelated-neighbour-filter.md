# `linkedto` — remove the correlated LATERAL branch

Design document · DiVoid bug [[#13004]] · root cause [[#13007]] · test-lane ruling [[#13006]]
Repo path: `docs/architecture/linkedto-uncorrelated-neighbour-filter.md`
Standards: Code Contracts **#114**, Design Contracts **#1136**.

**Line references** are to **HEAD `2a7ff78`** — the pre-fix tree this design targets. Every
`file:line` below was re-resolved against that ref on 2026-09-06. **Post-fix artefacts are cited by
name, not by line** — a test name is checkable by grep, and post-fix line numbers moved twice while
this document was being corrected.

**Corrections 2026-09-06, in three rounds.** Round 1, after QA [[#13012]]: three claims were **wrong**
and are corrected in place, each with a dated note at its site — §7's falsifier for G1, §9's suite
count, and §6's reader-inventory string sites. §7's coverage table was then re-measured against the
rebuilt guard and the manual Postgres lane. Round 2, after QA [[#13015]] CF3: §7's **G2b row** claimed
a property broader than its assertion pinned, and §7's closing paragraph carried a **negative
absolute** QA falsified by measurement. Round 3, after QA [[#13018]] CF4/CF5: G2b's Property was still
broader than the matcher on two axes — identifier spelling and the number of regions isolated — and
the closing paragraph over-reached for the **third consecutive time**.

**Round 3 is a re-derivation, not a third patch of the same shape.** Three rounds of defects that are
all artifacts of one mechanism are a finding about the mechanism, so §7 now asks whether a lexical
instrument is the right one for a structural property, answers it, and records the answer — see
*"Why the instrument is lexical, and stays lexical"* and *"Known limits of G2b"* in §7. The guard is
neither widened nor weakened; the claim comes down to what the guard measurably does, and the mutants
that survive it are enumerated rather than left for the next reviewer to rediscover.

One further change is **not** a correction: §2's and §9's `EmbeddingPatchSqlCompositionTests` /
`CLAUDE.md` items carry a **supersession note**, because the carve-out to [[#13009]] is a scope
decision taken after this document was written, not an error in it.

**Provenance of the round-2 text.** Draft prose for all three round-2 sites arrived from the
implementer during a round whose findings straddled the code and this document. It was treated as a
proposal, not as content: each of the three was re-derived against [[#13015]] and the shipped guard and
then **rewritten**, because unreviewed text in a maintained document is the same defect class as a
falsifier nobody ran. The substance of his diagnosis was right and is retained; three claims in his
draft were not, and are recorded in this document's round-2 note rather than carried forward.

---

## TL;DR

**Delete the LATERAL branch of `BuildLinkedToFilter`** (`NodeService.cs:484-492`) and with it the
`SupportsLateralJoin` gate. One shape on every engine: the uncorrelated
`node.id IN (link-subquery)` that the SQLite side already uses — and that `BuildLinkSubquery`
(`NodeService.cs:511`) already runs **ungated on Postgres today** for `?path=` hops.

**Why it fixes it:** the LATERAL correlation is an `OR` across two columns, so no hash semi-join is
reachable and *every* available plan is a per-outer-row scan of `nodelink`. Uncorrelated, the link set
is computed once. Measured on a production-replica graph, same seed set: **16.6 ms / 417 buffers vs
10,540 ms / 2,124,505** ([[#13007]]).

**Cost:** production code shrinks. Two Postgres tests about the two branches lose their subject — one
converted, one deleted.

**Rejected:** keep LATERAL, drop the inner `LIMIT 1` — measured insufficient at **3,547 ms**; the
`LIMIT` is the lever, the OR-correlation is the barrier.

**Out of scope:** `COUNT(*) OVER ()` — the 16.6 ms above was measured *with* it present.

---

## 1. The ask

> *"#13004 - check whether the bug is real, give me a short picture of the WHY and fix it"* — Toni,
> 2026-09-06.

The first two are done ([[#13004]] verdict `valid_bug`; [[#13007]] carries the WHY). This document is
the third.

**Requirement, in one sentence:** `GET /api/nodes?linkedto=…` must answer in a time bounded by the
seed set's neighbourhood for every seed set, and that must not depend on which plan Postgres picks.

### Size check (RULING 2026-09-03)

A one-sentence requirement gets a one-sentence solution. The remedy is **the deletion of one
conditional branch**. Net production-code delta is negative: one `if`, one `LoadOperation<NodeLink>`
construction, one `LateralJoin` call and one `PredicateExpression` return go away; nothing replaces
them. No new type, no new interface, no new configuration, no new dependency. If this document
proposed a mechanism, that ratio would be the finding — it does not.

---

## 2. Scope

**In scope**

| | |
|---|---|
| `BuildLinkedToFilter` (`NodeService.cs:482-502`) | delete the capability gate and the LATERAL arm; the uncorrelated arm becomes the whole method |
| `ListPaged` (`NodeService.cs:814`) | fixed by construction — shares the helper, no edit |
| `ListPagedByPath` / `ComposeHops` (`NodeService.cs:594`) | fixed by construction — shares the helper, no edit. Second, unreported call site; see §3 |
| `LinkedToLateralJoinTests.cs` | subject ceases to exist; one test converted, one deleted, `LateralCapabilityForcedFalseProxy` (`:159`) deleted. Post-fix the file itself is renamed `LinkedToPostgresManualTests.cs`, the LATERAL subject having gone |
| ~~`EmbeddingPatchSqlCompositionTests.cs`~~ | `[Explicit]` + `[Category]` conversion — settled by [[#13006]]. **Superseded 2026-09-06: carved out of this change into task [[#13009]]**, still open there. See the note under §9 |
| ~~`CLAUDE.md` §"Build & run"~~ | record the two test lanes — settled by [[#13006]]. **Superseded 2026-09-06: carved out of this change into task [[#13009]]**, still open there. See the note under §9 |
| New guards | §7 |

**Out of scope, each with the reason**

| | why |
|---|---|
| `COUNT(*) OVER ()` (`NodeService.cs:827`, `:901`) | §5 — measured non-load-bearing once the shape is uncorrelated |
| Index work on `nodelink` | the slow path consults no index ([[#13007]] §5) |
| Ocelot version bump (pinned `0.23.0-preview`) | not required; the remedy uses only what the fallback arm already uses |
| An **automatic** Postgres CI lane | [[#13006]] rules it "overkill"; the automatic lane stays SQLite |
| The peer project's `RecallScope` / 15 s client timeout ([[#13004]]) | different repo; 16.6 ms clears the timeout by three orders of magnitude |
| Refreshing `nodelink` statistics | [[#13007]] §6 — reproduces on freshly-ANALYZEd exact-replica stats; moves the threshold, never removes it |

---

## 3. The decision, and the necessity question it was asked to answer

The brief asked whether the capability-gated dual-branch structure is still earning its place. **It is
not**, on four independent grounds:

1. **It was never load-bearing.** [[#13007]] records task **#572** as an explicit cleanliness
   refactor — "collapse the UNION workaround" now that Ocelot shipped the primitive. No performance
   problem motivated it and none was measured.
2. **It is the sole cause of a severity-3 production defect**, and there is no good plan to tune
   toward. The OR-across-columns correlation makes hash/merge semi-join unreachable, so the entire
   reachable plan family is per-outer-row scans. Even the *fast* LATERAL plan costs **254,606 buffers
   to return a 20-row page** — 610x the uncorrelated shape's 417. "Fast" here means *less bad*.
3. **The uncorrelated shape is already the Postgres shape in this file.** `BuildLinkSubquery`
   (`NodeService.cs:511`, called from `NodeService.cs:563`) builds the identical
   UNION-of-two-directions link subquery **with no capability gate**, and every `?path=` hop has run
   it on Postgres since it shipped. The premise that Postgres needs LATERAL here is falsified by the
   repo itself.
4. **It is the only consumer of the gate.** `SupportsLateralJoin` appears in `Backend/` exactly once
   (`NodeService.cs:484`) and `LateralJoin(` exactly once (`:491`). Deleting the branch leaves no
   orphan capability check.

**Deleting a branch is the answer.** Per Design Contracts §4, refactoring what is already there is in
scope, and preserving an existing structure because it exists is how a small problem acquires a large
solution.

### Behavioural parity

The two arms compute the same set. LATERAL: `INNER JOIN LATERAL (… LIMIT 1) AS link ON TRUE` is a
semi-join — one row per node with ≥1 qualifying link — then `WHERE NOT node.id = ANY(seeds)`.
Uncorrelated: `node.id IN (both endpoints of every incident link) AND NOT node.id IN (seeds)`. Same
membership, same ordering, same paging; the `link` alias contributes no projected column. This parity
is exactly what `LinkedTo_ForcedFallback_Postgres_SameResultAsLateralBranch`
(`LinkedToLateralJoinTests.cs:114`) exists to assert, and it is why that test's subject disappears
with the branch rather than surviving it.

### The second call site

`ComposeHops` (`NodeService.cs:594`) passes the terminal operation through the same helper, so
`?path=…&linkedto=…` carries the identical failure mode. It is **in scope and costs nothing**: the
remedy edits the shared helper, so both call sites are corrected by construction. There is no reason
to narrow the fix to the reported one.

---

## 4. What the remedy guarantees — stated so it can be falsified

**Plan-independence: delivered absolutely.** An uncorrelated `IN (subquery)` admits no per-outer-row
subplan, so there is no family of plans among which one is 100x worse. The defect class is removed,
not tuned away.

**Neighbourhood-proportionality: delivered up to one linear pass over `node`.** The measured 417
buffers is the seed-scoped index access on `nodelink` plus a single sequential pass over `node`
(10,605 rows). That is `O(N_nodes)` **once**, not `O(N_nodes × N_links)`.

This document does **not** claim strict neighbourhood-boundedness, because it is not true.

> **Falsifier for the guarantee above:** a seed set on the production-sized graph whose `linkedto`
> response time differs from another seed set's by more than the ratio of their neighbourhood sizes.
> Under the remedy the residual `node` pass is a *constant* across seed sets, so any remaining cost
> difference must be neighbourhood-proportional. A future `node` table large enough for the single
> pass to matter would show up as a **uniform** slowdown across all seed sets — never as the
> combination-dependent flip [[#13004]] reports.

---

## 5. Why `COUNT(*) OVER ()` is out of scope

[[#13007]] ranks the paging total as a measured amplifier (3.5x in isolation) and as the source of the
planner's **530x** cost underestimate. Both are true of the LATERAL shape. Neither survives it:

1. **It amplifies a loop that will not exist.** The window is blocking, so `LIMIT` cannot stop the
   scan — that is only expensive when the thing being scanned is a per-outer-row subplan. Against a
   flattened semi-join the window aggregates the filtered result set, which the fix bounds.
2. **Cost-blindness needs a bad plan to hide.** A 530x underestimate of a nested loop matters because
   the planner then chooses it. With no nested-loop-over-`nodelink` plan reachable, there is nothing
   for the mis-costing to select.
3. **The remedy's headline number already includes it.** [[#13007]] captured the 16.6 ms / 417 buffers
   by running **the real `NodeService.ListPaged`** against the replica with `SupportsLateralJoin`
   forced false. `ListPaged` applies `DB.CountOver()` unconditionally (`NodeService.cs:827`).
   The measured fix is the fix *with* the amplifier present. This is the load-bearing reason, and it
   is measured rather than argued.
4. **Removing it costs an API contract.** `total` would become conditional, requiring a new query
   parameter — a knob with no named operator and no environment difference, which Design Contracts §3
   rules out.

**No follow-up task is filed for it.** Filing one would assert a future need that point 3 shows is not
established. If a `count`-heavy workload later makes the window measurable, it comes back with the
measurement in hand.

---

## 6. Design Contracts #1136 §5 — Pre-Design Checklist, answered in order

**KISS / DRY / YAGNI**

- **No new type mirroring an existing one.** No type is introduced.
- **No new abstraction with one implementation.** None introduced; one *branch* is removed.
- **No element justified by "we might need X later."** The `SupportsLateralJoin` gate is precisely
  such an element and it is being deleted, not preserved.
- **No deprecation period / feature flag / shim.** The change is atomic. There is no transitional
  shape in which both arms exist.
- **DRY math for the one duplication that remains.** After the fix, `BuildLinkedToFilter`
  (`NodeService.cs:496-500` at HEAD — post-fix, the surviving body of the same method) and
  `BuildLinkSubquery` (`NodeService.cs:511-517` at HEAD, unchanged by this fix) both build a
  UNION-of-two-directions link subquery. They differ in the `In(…)` argument type — a `long[]`
  literal versus a `LoadOperation<Node>` subquery — which resolves to different Ocelot overloads and
  different expression trees. **`block_size × site_count = 4 × 2 = 8`**, below the ~15-20 threshold
  (#1267), so they stay separate. Unifying them would require a generic seam whose only purpose is to
  paper over an overload difference. *Do not extract.*

**Existing systems first**

- **Existing surface audited.** The uncorrelated shape exists in this file, on this engine, ungated
  (`BuildLinkSubquery`). The remedy adopts it rather than inventing anything.
- **No new layer proposed**, so no "reason it can't live on the existing surface" is owed.
- **No new persisted data**, so the 4-week-named-decision gate (#868) does not apply.
- **Consumer chain recursed** for everything being deleted: `SupportsLateralJoin` → one consumer
  (`NodeService.cs:484`) → deleted. `LateralCapabilityForcedFalseProxy`
  (`LinkedToLateralJoinTests.cs:159`) → one consumer (`:132`) → deleted. Nothing survives with a dead
  reader.

**Configurability**

- **No new config knob.** The `COUNT(*) OVER ()` opt-out that §5 rejects would have been one; it is
  rejected on exactly this rule.
- **No magic numbers introduced or promoted.**

**Less is better**

- **Can-it-be-deleted, applied to the thing itself.** The whole design is the affirmative answer.
- **Can-it-be-deleted, applied to the guard (round 3).** Asked of the *stronger* guard that three
  review rounds kept pointing toward — an assertion over the Ocelot operation tree instead of over
  rendered SQL. With it absent, nothing observable breaks: the existing guard already reddens on the
  regression that actually recurs (a verbatim reinstatement), and every shape it misses is deliberate
  new code arriving in a diff. **It does not earn its place**, and the reasoning is in §7's *"Why the
  instrument is lexical, and stays lexical"*. Recorded here because a rejected mechanism is a design
  decision, and the next reader will otherwise re-propose it.
- **No new abstraction introduced in three correction rounds.** The rounds narrowed claims, enumerated
  measured limits, and added no type, helper, or layer. The one change round 3 asks of the guard is a
  precondition on an existing assertion, not a new instrument.
- **Trade-off named explicitly.** §4 states the residual linear `node` pass rather than claiming a
  guarantee the remedy does not deliver. §8 names the strongest rejected alternative with its
  measurement.
- **Radical-clean over compromise.** The unconsumed surface (the branch, the gate, the proxy, the
  branch-comparison test) is removed entirely rather than kept in a slimmer form. Per §4 of #1136,
  when neither extreme has a consumer the radical-clean choice wins.
- **Reader inventory covers AST *and* string references.** Re-derive rather than trust this list:
  grep the tree for `SupportsLateralJoin` and for `LateralJoin(`. AST: the two sites named in §3
  point 4. String: the identifier appears in prose at `LinkedToLateralJoinTests.cs:31`, `:103`, `:142`
  and `:155` — all comments or assertion messages inside a file this change already touches. None of
  them is a LowCode predicate, `Fields` array, or mapper indexer, so none can throw
  `UnknownFieldException`.

  > **Correction 2026-09-06.** An earlier revision of this bullet also listed the `BuildLinkedToFilter`
  > XML-doc as a string site, citing `NodeService.cs:470-481`. **It is not one, and the range was
  > wrong.** The block is `NodeService.cs:468-481`; it reads *"on databases that support LATERAL
  > JOIN"* in English and never spells the identifier, which occurs in `NodeService.cs` **exactly
  > once, at `:484`** — the AST site §3 point 4 already counts. Re-resolving the citation is what
  > found this. The doc block still has to be rewritten (§9 step 1) because it *documents the gate*;
  > it was never an additional reader.

**Data deliverables** — none. No SQL, no migration, no backfill.

**Document discipline**

- Cites #114 and #1136 as load-bearing (header).
- Scope inventories are explicit (§2), out-of-scope items listed rather than absent.
- No multi-paragraph rationale for things that obviously stay.
- **Supersession:** this document supersedes nothing. #572's design is not a repo document; its
  provenance is recorded in [[#13007]] and this document links to it.

---

## 7. Coverage — the guard, per row

A row names a test, and the falsifier column states **only mutations whose output has been observed**
— where none has been, the cell says so ([[#1220]] §5 addendum 2026-09-05). **All five rows now have
one**, which was not true of the revision this document shipped first, and every falsifier is now
quoted from a node rather than from a report: [[#13012]], [[#13015]] and [[#13018]] for the automatic
rows, [[#13010]] for the manual one.

**A falsifier column records what a guard catches; it is not a statement of what a guard covers.**
G2b's known survivors are enumerated in *"Known limits of G2b"* in this section, and the reason that
row is bounded the way it is rather than widened again is in *"Why the instrument is lexical, and
stays lexical"*. Read the two together or the table reads stronger than it is.

| # | Property | Guard | Lane | Falsifier — status |
|---|---|---|---|---|
| **G1** | The Postgres-dialect SQL for `?linkedto=` contains **no `LATERAL`** | the `G1:`-labelled assertion in `LinkedTo_PostgresDialect_UncorrelatedUnionShape_NoLateral` (`LinkedToSqlShapeTests.cs`) | **automatic** (SQLite lane — no connection, no container) | **Measured red** against **M5**, the full pre-fix revert — HEAD's `BuildLinkedToFilter` plus its two-argument call sites. It **compiles** against this guard and reddens G1, G2a and G2b together, the captured SQL showing `INNER JOIN LATERAL (...) AS link ON TRUE`. Quoted verbatim in [[#13015]] and re-derived in [[#13018]]; first reported in [[#13013]]. This is the acceptance property for the guard itself, and it is the row's whole point. |
| **G2a** | The Postgres-dialect SQL for `?linkedto=` **is** the uncorrelated form — the link subquery appears under `IN(` with `UNION ALL` | the `G2a:`-labelled assertion in the same test | **automatic** | **Measured red** against **M3** (remove the `.Union(…)` arm) — no `UNION ALL` in the captured SQL. Re-run against the rebuilt guard, single-test-isolated, `-t:Rebuild` per row ([[#13013]]); QA's independent run of the same mutant agreed ([[#13012]]). |
| **G2b** | The **first** parenthesised group containing `FROM nodelink` — which in the shipped shape spans both union arms — carries no occurrence of the lowercase token `node` followed by `.`, in **either operand position** | the `G2b:`-labelled assertion in the same test, scored against that isolated group rather than against the whole statement | **automatic** | **Measured red** against both orderings of the reintroduced correlation: **M4**, alias on the right, rendering `"sourceid" = node."id"`, and **M6**, alias on the left, rendering `node."id" = "sourceid"` — both quoted with captured SQL and an `Expected:` / `But was:` pair in [[#13018]], which also re-derived M6 independently. Also red under **M5**. **Three mutants are measured green against it — see *"Known limits of G2b"* in this section.** Those limits are the row's boundary, not a gap awaiting a patch. |
| **G3** | Multi-seed `linkedto` returns the union of the seeds' neighbourhoods, minus the seeds | `ListPaged_FilterByLinkedTo_MultiSeed_ReturnsUnionOfNeighboursExcludingSeeds` (`NodeServiceTests.cs`) | **automatic** | **Measured red** against **M1** (drop `&& !n.Id.In(linkedTo)` — seeds leaked into results) and against **M3** (missing neighbours) ([[#13013]]; QA's run agreed, quoting `ids.Intersect(seeds)` as `<1,2,3>` and `SupersetOf` as `<empty>` — [[#13012]]). **This supersedes the earlier cell**, which read *"no runnable falsifier established"*. |
| **G4** | `linkedto` returns correct neighbours against **real Postgres** | `LinkedTo_Postgres_FindsNeighbourExcludesSeed` (post-fix, in `LinkedToPostgresManualTests.cs`), **converted** per this row: renamed off "LateralBranch", `[Explicit]` + `[Category("PostgresManual")]`, `Assert.Inconclusive`/env-var gate replaced, and the `SupportsLateralJoin, Is.True` assertion (`LinkedToLateralJoinTests.cs:102` at HEAD) deleted — it asserts the thing being removed | **manual smoke** (Docker Postgres, [[#13006]]) | **Measured red three times, each on a different assertion** — operator's lane run, PostgreSQL 16.15 ([[#13010]]): **M-A** drop the seed-exclude → `Expected: not some item equal to 4 / But was: < 4, 5 >`; **M-B** drop `n.Id.In(linkOp) &&` → `Expected: not some item equal to 9 / But was: < 8, 9 >`; **M-C** flip `\|\|` to `&&` in both union arms → `Expected: some item equal to 14 / But was: <empty>`. Pristine restored green, 1/1, 409 ms. The row is **per-assertion** covered, not merely test-level. **This supersedes the earlier cell**, which read *"no runnable falsifier established"*. |

**Premise that makes G1, G2a and G2b discriminate:** `PostgreInfo.SupportsLateralJoin` is `true` and
`SQLiteInfo.SupportsLateralJoin` is `false` — measured by reflection against
`Pooshit.Ocelot 0.23.0-preview` (the property is declared on `DBInfo` and overridden on `SQLiteInfo`).
So a mocked `IDBClient` returning `PostgreInfo` drives the production code down the Postgres arm with
no database present. That is what makes a Postgres-shape guard runnable in the SQLite lane.

**Premise that makes G2b discriminate, and the direction it runs in:** the outer alias in this query
is literally `node`, fixed by `NodeMapper.CreateOperation`'s `.Alias("node")` (`NodeMapper.cs:125`,
untouched by this change), and Ocelot renders a reference to it as `node."col"`.

The implication that gives is **one-way, and only one way**: a `node.` token inside the isolated group
is sufficient evidence of a correlation to the outer row. **Its absence is not evidence that none is
there.** An earlier revision of this paragraph said the token property and the structural property
*"coincide"* for this query; that is a biconditional and [[#13018]] W2 measured it false — a
correlation spelled `NODE."id"` or `"node"."id"` names the alias, correlates, and carries no `node.`
token. Postgres folds all three spellings to the same identifier and returns the same rows.

So G2b is a **detector, not a decision procedure.** That is the whole shape of what this row can be,
and *"Why the instrument is lexical, and stays lexical"* in this section is the argument for why that
is the right thing to build here rather than a defect to fix.

**The G1 premise survived review:** QA's M5-c ([[#13012]]) reinstated the branch and rendered
`INNER JOIN LATERAL ( … ) AS link ON TRUE` under exactly this mock. The premise was always sound; the
first harness built on it was not, and it has since been rebuilt to capture the SQL at the injected
`IDBClient` boundary while driving the fully public `ListPaged` ([[#13013]]). The mocked-`PostgreInfo`
dialect is the part that carried over unchanged.

### Correcting one inherited claim, because the whole coverage answer turns on it

[[#13006]] and QA **#580** state that SQLite CI "can never exercise this branch". **The branch was
never exercised; it was always reachable.** The mocked-`DBInfo` render harness at
`SemanticSearchFilterCompositionTests.cs:68` reaches Postgres-dialect code generation with zero
infrastructure, and its fixture runs green in **154 ms** at HEAD `2a7ff78` (10/10, measured). #580's
substitution proof measured the absence of tests, not an impossibility.

This does not weaken [[#13006]] — its ruling is about the *lanes*, and the lanes stand. It changes
what this fix needs: **no new Docker fixture is required for this bug.**

### Correction 2026-09-06 — G1's falsifier was a prediction, and the guard built from it cannot fail

The G1 row previously closed with *"Reinstating the branch reddens it."* **The render quoted ahead of
it was a real measurement; that closing sentence was a prediction, and it is false of the guard that
shipped.** Measured by QA ([[#13012]] CF2) against the working tree on base `2a7ff78`:

| probe | observed |
|---|---|
| reinstate `BuildLinkedToFilter` verbatim from HEAD | the guard **does not compile** — `error CS1061`. It was never once observed red |
| the same, with the guard file removed, full suite | **691 / 691 green** — the whole behavioural suite is blind to the defect |
| the same, with the guard's *call* adapted to pass the operation in | all three assertions red; rendered SQL carried `INNER JOIN LATERAL ( … ) AS link ON TRUE` |

**How the false cell was produced.** The render quoted in the old cell came from a harness that passes
`ListPaged`'s operation **into** production and renders what production composed. The guard that
shipped assembles the operation in the test and asks production only for a predicate, so
`Does.Not.Contain("LATERAL")` is unfalsifiable by construction — a `PredicateExpression<Node>` cannot
carry a join. **The falsifier was true of what this document measured and false of what exists**, and
the cell never said which harness the measurement came from. The probe that adapts the call to pass
the operation is what makes this diagnosable: the assertions discriminate; the call path does not
reach them.

[[#1220]] §5 addendum 2026-09-05 is the rule this broke — *a falsifier cell may only name a mutation
whose observed output someone has quoted to you; if none exists, the cell reads "no runnable falsifier
established."* The G1 cell now reads that way, and will until someone quotes an observed red.

**Properties the replacement guard must satisfy.** Stated as properties, not as a mechanism: the
mechanism is the implementer's, and [[#13012]] established a route this document did not know about —
`IDBClient` is **already injected** into `NodeService` and **already substituted** by this fixture, and
the deleted `LinkedTo_ForcedFallback_Postgres_SameResultAsLateralBranch` already demonstrated the
shape through the public surface.

1. The asserted SQL is rendered from the operation **production built**, not from one the test
   assembled.
2. The guard **compiles and goes red** against a verbatim reinstatement of HEAD's
   `BuildLinkedToFilter`. That is the acceptance test for the guard itself.
3. No production member's accessibility widens to make it reachable ([[#13012]] CF1; #114 RULING
   2026-08-08).
4. The negative assertion is preceded by a positive one — assert first that SQL was genuinely
   captured and carries the linkedto subquery, **then** assert the absence of `LATERAL`. A relocated
   negative keeps its wording and silently loses its meaning (#11140).

**Discharged 2026-09-06.** All four are met by the rebuilt guard, and the G1 row of this section's
coverage table now names it and quotes an observed red. Per [[#13013]]: the guard drives the fully public `NodeService.ListPaged`
and captures the command text at the injected `IDBClient` boundary (properties 1 and 3 — no
hand-assembled operation, and `BuildLinkedToFilter` is back to `private`); the full pre-fix revert
**compiles** against it and reddens all three shape assertions, where the guard this note is about
could not be compiled at all (property 2); and the capture-positive assertions run before the shape assertions
(property 4). **This paragraph is the record of what was wrong, not a live gap** — it stays because the
failure mode is worth keeping, not because the guard is still missing.

### Correction 2026-09-06 (round 2) — G2b pinned a pattern, not the property

**The defect, quoted from the measurement rather than described.** G2b's assertion pinned the literal
`= node."id"`, while this row's Property cell and §7's closing paragraph both claimed *"no reference to
the outer `node` alias"*. QA's **M6** reintroduced the same correlation with the alias on the **left**
of the comparison, rendering `node."id" = "sourceid"` inside the `IN` subquery. It built clean and
[[#13015]] records it as *"SURVIVED, on every guard in the branch"* — capture-positive, G1, G2a, G2b
and G3 all green. The result set is unchanged, because arm 2 of the union still supplies every
neighbour, so the behavioural guards cannot see it by construction; **the shape guard was the only
instrument that could, and the assertion it was carrying could not.**

That matters here rather than being a coverage nit: per [[#13007]], the correlation *is* the barrier
that makes every reachable plan a per-outer-row scan. M6 reintroduces the defect class while leaving
the output identical — a plan pathology with an unchanged result set.

**Why the remedy is a scoped property rather than a second literal.** A literal-match guard constrains
exactly the spellings someone thought to enumerate, which is the property CF3 measured: one was
enumerated, the mirror ordering was not. Adding the mirror would restore that specific coverage without
changing the kind of claim the row can support. The shipped assertion instead isolates the link
subquery — both union arms — from the rest of the captured command and asserts that no `node.`-qualified
token occurs anywhere within it, in either operand position. That is checkable against a region rather
than against an enumeration, which is why the Property cell can now state it without listing spellings.
**It is still a claim about tokens**, bounded by the *"Premise that makes G2b discriminate"* recorded
with the coverage table; it is not a proof that no correlation is expressible by any other means.

**Discharged 2026-09-06, and independently re-derived.** Against the shipped assertion **M4** is red,
**M6** is red, and **M5**'s full pre-fix revert reddens all three shape assertions together. When this
note was first written M6's red rested on the implementer's report alone; [[#13018]] has since re-run
it, and quotes the captured SQL and the `Expected:` / `But was:` pair from its own run. **Every
falsifier on this row is now readable in a node.**

**What round 2 fixed, and what it did not.** The mechanism that came out of it — isolate a region,
assert a token property over it — is the right shape and [[#13018]] confirms it does what CF3 asked.
The *claims* attached to it were still broader than the matcher on two further axes, which is CF4 and
CF5 of that review. Those are answered in *"Known limits of G2b"* and *"Why the instrument is lexical,
and stays lexical"* in this section, rather than by a third widening of this note.

### Known limits of G2b — measured, enumerated, and deliberately not patched

[[#13018]] ran three mutants that **survive G2b green** while reintroducing a per-outer-row
correlation, each confirmed on PostgreSQL 16 to return the identical row set and to produce the
per-outer-row plan [[#13007]] identifies as the barrier:

| mutant | what it does | why G2b is green |
|---|---|---|
| **M8** | correlates with the alias spelled `NODE` — renders `NODE."id" = "sourceid"` | the matcher pins the lowercase token; Postgres folds the identifier, the matcher does not |
| **M8q** | correlates with the alias spelled `"node"` — renders `"node"."id" = "sourceid"` | same, with the quote character breaking the token boundary |
| **M9** | leaves the shipped union untouched and ANDs in a **second**, correlated link subquery | the isolation takes the **first** `FROM nodelink` group; the second is outside the region — and it carries the lowercase `node.` token G2b names, in both operand positions |

**These are limits, not outstanding defects.** They are recorded here so that the next reader of this
row knows its boundary without re-deriving it, and so that a future change that adds a second link
subquery — *"only direct links"*, *"only links of type X"*, the shape [[#13018]] correctly identifies
as the most plausible way this file grows — meets the limit in writing rather than in production.

**The exact property the assertion must state — no more, no less.** The message beside G2b, and any
prose describing it, asserts this and stops:

> the **first** parenthesised group containing `FROM nodelink` in the captured command contains no
> occurrence of the lowercase token `node` immediately followed by `.`, allowing intervening
> whitespace — in either operand position of a comparison.

Three bounds are load-bearing and none may be dropped: **first group** (not every group), **lowercase
`node`** (not any spelling of the alias), **token adjacency to a dot** (not "a reference to the outer
relation", and not "no correlation"). What it must *not* claim: spelling-independence, coverage of
more than one region, or the absence of correlation as such.

**One change to the guard is warranted, and it is not a widening.** [[#13018]] W1 measured that the
isolated region can degenerate — given a parenthesised expression in the projection, the region
collapses to a fragment and **G2b passes vacuously while `node.` is present in the query**. That is a
different failure from M8, M8q and M9: those are the guard catching less than someone hoped, this is
the guard silently not running. It is the same class as the capture-positive assertions the fixture
already carries for [[#11140]], and it closes with one precondition — **the isolated region must
itself contain `FROM nodelink`**. Latent today, because Ocelot renders bare column names in the
projection; worth closing because a vacuous assertion is the failure mode this document has already
paid for once (see the G1 correction in this section).

### Why the instrument is lexical, and stays lexical

The property this row is about is structural — *no correlated reference to the outer relation* — and
the instrument is a regex over rendered text. That mismatch is real, and three review rounds have each
found a new way through it: an operand ordering ([[#13015]] CF3), two identifier spellings and a
second region ([[#13018]] CF4, CF5). **Successive defects that are artifacts of one mechanism are a
finding about the mechanism**, so the mechanism was re-derived rather than patched a fourth time.

**What this guard is for.** The remedy in this document is a **deletion**: one `if`, one
`LoadOperation`, one `LateralJoin` call. Nothing was added. The regression it guards against is that
deletion being undone. Every mutant named in this section — M4, M6, M8, M8q, M9 — is **deliberately
written new code**
that would arrive in a diff and be reviewed on its merits; M9 is fifteen new lines constructing a
second subquery. None of them is a shape this method drifts into. What *does* plausibly recur is the
pre-fix code coming back, and that is **M5**, which reddens G1, G2a and G2b together.

**The structural instrument was considered and rejected on cost.** Asserting over the Ocelot operation
tree instead of the rendered string would catch all five uniformly. It would also require walking
`Pooshit.Ocelot`'s internal predicate and alias representation — a pinned **preview** package whose
internals are not a contract — to guard an eight-line method (`NodeService.cs:472-479` *(post-fix)*)
whose body is two statements. That buys a
test that breaks on package upgrades for reasons unrelated to this bug, and it fails #1136 §4's
can-it-be-deleted question: with the stronger guard absent, nothing observable breaks, because the
weaker one already reddens on the regression that actually recurs.

**Widening the lexical guard again was rejected on kind, not on cost.** Catching `NODE` and `"node"`
and scanning every region would close M8, M8q and M9 — and would leave the claim lexical, so the next
review finds the next evasion. A lexical matcher cannot enumerate the complement of the class it
names; four rounds is evidence of that, not of insufficient care.

**So the guard stays as it is and the claim comes down to meet it.** A stated limit is a design
deliverable and a reader can act on it. A completeness claim nobody can hold is not, and this document
has now shipped three versions of one.

### Why no plan-level guard is specified

The natural row — *"the Postgres planner does not choose a per-row `nodelink` scan"* — is deliberately
absent. [[#13007]] observed the flip only at production graph scale (10,605 nodes / 31,453 links); a
small `[Explicit]` fixture would very likely report **green against a reinstated LATERAL branch**,
because the bad plan is not chosen at small `nodelink` cardinality. A guard that fires green on
non-compliant code is worse than absent (§9 addendum 2026-09-02). Reproducing production scale inside
a test fixture is not proportionate to a one-branch deletion.

**That is the whole argument, and it does not rest on the shape guard.** A plan-level fixture is
rejected because a small one would report green on non-compliant code and a production-scale one is not
proportionate to a one-branch deletion — both true independently of how good the shape guard is.

**What the shape guard adds, and nothing beyond it.** G1, G2a and G2b are textual properties of the
command production sends to `IDBClient`. Their measured value is one fact: a **verbatim reinstatement
of the pre-fix code reddens all three together** ([[#13018]] M5, captured SQL quoted; also in
[[#13015]]). That is the regression this change can actually suffer, and it is caught.

**This paragraph names no class of shapes and claims no coverage of one.** Its three previous versions
each did, and each was falsified by measurement: an absolute (*"can no longer be generated without a
red build"* — [[#13015]] CF3), then a partition (*"the two routes"*), then a universal over a named
class (*"so both are caught"* — [[#13018]] CF5, where a member of the named class produced the named
token and passed). Each version was narrower than the last and each still outran the instrument. The
guards' exact reach is enumerated in *"Known limits of G2b"* in this section, together with the
mutants that are measured green against them; **the argument for leaving it there rather than
widening it a fourth time is in *"Why the instrument is lexical, and stays lexical"*.**

**The case for omitting a plan-level guard does not rest on any of this**, which is why it survives all
three corrections unchanged: a small fixture reports green on non-compliant code, and a
production-scale one is disproportionate to a one-branch deletion. Both were true before the shape
guard existed and remain true at whatever strength it settles.

---

## 8. Rejected alternatives

| Alternative | Why not |
|---|---|
| **Keep LATERAL, drop the inner `LIMIT 1`** (`EXISTS` form) | **Measured insufficient: 3,547 ms** — Nested Loop Semi Join, 2,695,362 rows removed by join filter ([[#13007]] §1). The `LIMIT` makes the worst reachable plan look cheapest; it is not why the good plan is unreachable. The OR-correlation is. *This is the strongest rejected alternative.* |
| **Keep LATERAL, rewrite the correlation as two equijoins** (`sourceid = node.id` UNION `targetid = node.id`) | Reachable in principle, but it arrives at the same two-direction union the fallback already expresses — via a LATERAL wrapper that adds nothing. Strictly more machinery for the same set. Fails #1136 §4 can-it-be-deleted. |
| **Address `COUNT(*) OVER ()` instead** | §5. Measured 3.5x in isolation, and the remedy's 16.6 ms was measured with it present. |
| **Index work on `nodelink`** | The slow path uses no index at all ([[#13007]] §5); the flip is *away from* index access. No index shape is consulted. |
| **Refresh / pin `nodelink` statistics** | [[#13007]] §6 — reproduces on freshly-ANALYZEd exact-replica stats. Moves the threshold, never removes the vulnerability. |
| **Keep both arms, gate LATERAL behind a config flag** | A knob with no named operator and no environment difference (#1136 §3), guarding a branch with no measured benefit in any configuration. Doubles the test matrix to preserve a defect. |

---

## 9. Implementation order

1. **`BuildLinkedToFilter`** (`NodeService.cs:482-502`) — delete the `if` (`:484`) and the LATERAL arm
   (`:486-492`); the former `else` body (`:496-500`) becomes the method body. Update the XML-doc
   (`:468-481` — the whole `<summary>` block; an earlier revision cited `:470-481`, which starts
   mid-sentence), which currently documents the gate.
2. **Verify nothing orphaned** — `SupportsLateralJoin` and `LateralJoin(` must have zero occurrences
   left in `Backend/`.
3. **G1 + G2a + G2b** — the SQL-shape guard, specified as properties rather than as a mechanism: the
   four it must satisfy are listed under *"Correction 2026-09-06 — G1's falsifier was a prediction"*
   in §7, where they are also recorded as met. The harness at
   `SemanticSearchFilterCompositionTests.cs:68` / `:87` is this document's **proof that
   Postgres-dialect rendering is reachable offline** (§7) — it is not a harness to copy. Copying its
   call shape is what produced the first G1 guard, which could not fail ([[#13012]] CF2); the
   property that fixed it was to capture at a boundary production itself uses.
4. **G3** — the multi-seed behavioural test in `NodeServiceTests.cs`. Existing `linkedto` coverage
   (`:648`, `:1045`) is single-seed only, and the reported defect is multi-seed.
5. **`LinkedToLateralJoinTests.cs`** — convert test 1 per G4; delete test 2 (`:114`) and
   `LateralCapabilityForcedFalseProxy` (`:159`), whose only consumer is test 2 (`:132`).
6. ~~**`EmbeddingPatchSqlCompositionTests.cs`** — `[Explicit]` + `[Category]` conversion of the 7
   Postgres-gated tests, replacing the env-var / `Assert.Inconclusive` pattern ([[#13006]]).~~
7. ~~**`CLAUDE.md` §"Build & run"** — record the two lanes and the command that selects the manual
   one.~~

> **Supersession note, 2026-09-06 — steps 6 and 7 are no longer part of this change.** They were in
> scope when this document was written. The operator then split them out into task [[#13009]] so this
> change ships one feature, and QA upheld the split ([[#13012]]). **Neither item was dropped and
> neither was done here** — both are open in [[#13009]], and the [[#13006]] ruling behind them is
> untouched. This is a later scope decision, not an error in this document.

Steps 1-2 are the fix. Steps 3-5 are its guards. Together they are this change's whole scope.

**Expectation for step 1's effect on the existing suite:** the SQLite lane already exercises the
surviving arm, so no existing automatic test should change verdict. If any does, that is a finding to
report rather than to accommodate.

> **Correction 2026-09-06.** This paragraph previously opened *"591 tests currently pass."* **That
> figure is wrong and was never measured.** Measured by QA ([[#13012]]), Release, full suite:
> **690 passed / 0 failed at HEAD `2a7ff78`**, and **692 passed / 0 failed** on the post-change
> working tree — a delta of exactly the two tests this change adds, which is the expectation this
> note is attached to, holding. A count is a property of a specific tree at a specific ref: quote the ref with the figure,
> or do not quote the figure. **It propagated, and that is the part worth recording** — the
> implementer carried the 591 into his return and attributed it to the operator's brief, which
> carried no count at all ([[#13012]], "Claims checked"). A wrong figure travelled with a wrong
> provenance, moving the error off the deliverable that actually held it.

---

## 10. Open questions

**None blocking.** Two things surfaced rather than decided:

1. **The "SQLite CI can never exercise this branch" claim in [[#13006]] and #580 is too strong** — see
   §7. Worth correcting in [[#13006]] so the next capability-gated branch is not written off as
   untestable. Not a change to that node's ruling.
2. **`BuildLinkSubquery`'s `UNION ALL`** returns duplicate endpoint ids that Postgres deduplicates
   inside the semi-join. Changing it to `UNION` (distinct) is *not* proposed: [[#13007]]'s 16.6 ms was
   measured against the `UNION ALL` form, and altering it would substitute an unmeasured shape for a
   measured one.
