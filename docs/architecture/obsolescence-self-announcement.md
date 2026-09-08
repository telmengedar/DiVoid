# Design: obsolescence self-announces at the point of remembering

> Repo path: `docs/architecture/obsolescence-self-announcement.md` — this file and DiVoid node **#13307** are the same document. An edit to one is not finished until the other matches it.
> Task: DiVoid **#7217** · Edge semantics: **#7216** · Repo map: **#5860**
> Base: `main` @ **`ab03c3a`**. Every measurement below was taken against **production on 2026-09-08**. Commands are in §16.

## TL;DR

**Make labelled edges default-on in `divoid_search` and `divoid_list`.** Every result row carries its incident edges *that have a `context`* — no verb allow-list, no backend change. One predicate (`if link.get("context")`) in `_link_details.py`, called from both tools. `include_link_details=True` keeps its meaning and widens the row to *all* edges.

**Cost, measured three times independently:** search rows **1.23×–1.59×** (~150–370 tokens/search; **~1.3× working figure**, my 1.59× came from a sample biased toward this task’s own subject matter); `divoid_list(count=200)` 1.07–1.16×; latency +121 ms on 1,411 ms. All-edges default-on rejected at **11.8×** — one 10-row search measured 114,903 bytes.

**Two fixes ride along, both forced by default-on:** today `include_link_details=True` returns `severity` and `rootNodeId` as `null`; and appending `linkDetails` without `id` silently returns **zero** edges (#12953).

**Not building:** `resolve_canonical` — measured supersession chain depth is **1**, so the row already carries the tip.

---

## 1. Problem Statement

Toni, 2026-07-28, verbatim in #7217:

> *"whenever agents look at nodes / list nodes, they should load the link details as well so they immediately see when something is obsolete... I don't want truth by curiosity — they should automatically have the clear correct structure when loading these nodes... like 'hey, my content exists but is outdated, please look at the other node.'"*

The 2026-07-29 refinement moved the primary surface from `get_content` to **search/list**, because remembering starts with a search and you arrive at a node *through* it.

What shipped satisfies the letter and not the sentence. `include_link_details` exists on both tools — **defaulting to `False`** (`search.py:73`, `list_nodes.py:354`). That relocates the remembering from *"call `divoid_get_links`"* to *"pass `include_link_details=True`"*. It is the same class of failure one step cheaper, and #7217's triage measured it failing: one long session ran dozens of searches, passed the flag **zero** times, and created eight `supersedes` edges that the shipped default will never surface.

**Success criterion.** An agent that runs an ordinary `divoid_search` — no flags, no remembered discipline — sees, on the row itself, that a node has been superseded and by which node.

---

## 2. Scope & Non-Scope

**In scope**

- Default-on labelled-edge enrichment on `divoid_search` and `divoid_list` (#7217 item 1).
- The two defects default-on forces (§8): the `severity`/`rootNodeId` nulling, and the missing-`id` adjacency trap.
- The tool-description changes that make the new default legible to a model.
- A decision, with reasons, on #7217 items 2 and 3.

**Explicitly out of scope**

| Out | Why |
|---|---|
| Any `Backend/` change | The projection this needs already exists and is already batched (§10). Nothing is missing server-side. |
| Fixing #11417 / #12953 in the backend | Separately designed in `docs/architecture/fields-projection-nullability.md` / node **#12952**. This design works around #12953 MCP-side (§8.2) and does not pre-empt that fix. |
| `resolve_canonical` (#7217 item 2) | Recommended not built — §9.1. |
| `get_content` / `get_node` enrichment (#7217 item 3) | Recommended not built — §9.2. |
| Editing #7216 / #190 / #9 / the Primer | Named as a required follow-up in §11; those are DiVoid nodes, not repo files, and are the operator's to publish. |
| A directional `linkedto` or any query-side edge filter | Not asked for; #7216 records direction as read-metadata, not a traversal gate. |

---

## 3. What is actually true at the base

The brief supplied four claims "from grep, not from reading the tools end to end". All four verified at `ab03c3a`. `git diff --stat 344f835..HEAD` touches only `Backend/Services/Nodes/NodeService.cs`, `Backend.tests/Tests/NodeAccessHttpTests.cs` and one doc — **no MCP file changed**, so the figures also hold at `344f835`.

| Claim | Verdict | Evidence |
|---|---|---|
| `status` is default-on in `divoid_search` rows | **True** | `search.py:206` reads it unconditionally; the backend's default projection carries it. `GET /nodes?query=…&count=1` returns `"status":"open"` for #7217. |
| `include_link_details` exists on both tools, opt-in | **True** | `search.py:73` and `list_nodes.py:354` (internal `_execute` at `:208`), all `bool = False`. |
| `resolve_canonical` does not exist | **True** | `grep -rn "resolve_canonical\|resolveCanonical"` over the repo: 0 hits. 22 tools registered in `tools/__init__.py`. |
| `get_content` / `get_node` enrichment does not exist | **True, with a correction** | `get_content` returns body + content-type only. But **`get_node` already returns `status`** (`get_node.py:76`) — so "get_node is blind to obsolescence" is not accurate, and that changes §9.2. |

**One nuance worth recording**, because it looked like a defect and is not: the backend omits null members from the JSON, so a node with no status has no `status` key on the wire at all. `n.get("status")` correctly yields `None`. A first probe of mine read a documentation row's key set, saw no `status`, and briefly concluded the field was not projected. It is.

---

## 4. Measurements

All bytes are **MCP-emitted bytes** — the tool result the model pays tokens for, rebuilt with `search.py`'s own row shape (`search.py:200-219`) — not HTTP wire bytes. Wire bytes do not enter a context window; that distinction carries §5 and §10.

### 4.1 The graph, 2026-09-08

10,863 nodes; **32,469** distinct edges enumerated over every node id.

| Property | Value |
|---|---|
| Edges carrying a `context` | **2,014 (6.20%)** |
| `linkType` | `None` 30,598 · `Unidirectional` 1,862 · `Bidirectional` 9 |
| Contexts ≤24 chars (verb-shaped) | 984 edges, 380 distinct |
| Contexts >24 chars (free prose) | 1,030 edges, 965 distinct |
| Degree per node | median **4**, p90 12, p99 34, **max 1,069** (`#325 Docs`) |
| `context` string length, over the 2,014 | median **25**, p90 72, p99 144, max **251** chars |

Top verb-shaped contexts: `supersedes` 63, `implements` 62, `reviews` 51, `depends-on` 36, `fixes` 25, `follows` 20, `subtask-of` 20, `requested-by` 20.

### 4.2 Candidate selectors, 10 queries × 10 rows

**The sample, disclosed** — it was omitted from the first version of this document and that omission is what made the figures below irreproducible (QA #13312; §16). These are the ten queries, verbatim and in order:

```
obsolescence protocol supersedes edges
how does the node link projection work
what is the convention for filing tasks
divoid-mcp tool surface and invariants
backend patch access gate
code contracts for the backend
QA review rounds and critical fails
repo map for the frontend
how do I file a task with the right status
design document for anchor grounded recall
```

**They are not a neutral sample.** Every one of them is drawn from this task's own subject matter — edges, supersession, contracts, QA rounds — so they rank toward the most heavily cross-linked and most heavily discussed region of the graph, which is exactly the region where labelled edges are densest. **The multiplier below is therefore an upper bound, not a central estimate.** Two independent runs on other samples measured lower; see the table after it.

| Selector | mean B | median B | max B | ×S0 | edges/row median | edges/row max | rows firing |
|---|---|---|---|---|---|---|---|
| **S0** none (today's default) | 2,502 | 2,514 | 2,994 | 1.00× | – | – | – |
| **S1** `context ~ "supersede"` | 2,787 | 2,760 | 3,561 | 1.11× | 0 | 2 | 4/100 |
| **S2** `context is not null` | 4,108 | 3,776 | 8,657 | **1.64×** | 0 | 10 | 34/100 |
| **S3** all edges (today's `=True`) | 29,523 | 19,042 | **114,903** | 11.80× | 9 | 1,059 | – |

S3's worst case (`"what is the convention for filing tasks"`) pulls in the `Tasks` hub `#324` at 1,059 edges. **That is why all-edges default-on is not on the table.**

With the §8.1 fix applied (`severity`/`rootNodeId` restored to the projection) and the §7.3 omit-if-empty rule, this sample gives **S0 2,481 → S2 3,956 bytes, 1.59×**, worst case 8,635.

**Measured independently, three times, and 1.59× is the outlier:**

| Run | Sample | S2 × S0 |
|---|---|---|
| this document | the 10 queries above, all drawn from this task's subject matter | **1.59×** |
| implementer | not disclosed to me | **1.30×** |
| QA (#13312) | not disclosed to me, aggregate | **1.232×** |

The method is not in dispute — QA's S0 reproduced mine **to 3 bytes** and the S3 worst case to **0.3%** on the query this document names. The spread is the *sample*, and mine is the biased one for the reason given above. **Take 1.23×–1.59× as the range and ~1.3× as the working figure**; §6 derives its costs from the range rather than from 1.59×.

### 4.3 `divoid_list` at scale

| Query | rows | S0 | S2 always-emit | S2 omit-if-empty |
|---|---|---|---|---|
| `type=task status=open` | 200 | 42,475 | 52,827 (1.24×) | **49,427 (1.16×)** |
| `root_node_id=3` | 200 | 34,535 | 40,823 (1.18×) | **37,083 (1.07×)** |

List is markedly cheaper than search because search ranks toward hub-adjacent, heavily-discussed nodes. Omit-if-empty pays for itself here (`"link_details": []` is 20 bytes × every row) and barely registers on search (132 B/search) — it is one token of code either way.

### 4.4 Latency, 5 runs each

| | runs (ms) | median |
|---|---|---|
| plain | 1422, 1411, 1411, 1375, 1392 | **1,411** |
| `fields=…,linkDetails` | 1504, 1552, 1532, 1576, 1475 | **1,532** |

**+121 ms, +8.6%**, on a query whose page pulls 1,882 edge rows — near the worst case the graph offers. `FetchAdjacentEdges` is one batched query for the whole page (`NodeService.cs:675`), not per row.

### 4.5 Does the obsolescence protocol hold on real data?

This is the measurement that changed the design, and it contradicts an assumption I nearly shipped.

#7217's triage says *"eight `supersedes` edges"*; it enumerates **seven** source→target pairs. Seven is what I measured.

| Population | `status` on the stale side | `supersedes` edge | content banner |
|---|---|---|---|
| Nodes with `status=obsolete` (**2 graph-wide**: #7443, #7187) | `obsolete` 2/2 | **2/2** | **2/2** |
| The 7 pairs #7217's triage names | `closed` ×6 (#1434, #1638, #11369, #8440, #11388, #9876); 7th is a data defect, below | 7/7 | **0/7** |

**#7216's three-part protocol is only fully available on documentation, and #7216 does not say so.** Precisely: it instructs *"use ALL THREE when you supersede a node"* without qualification, while its own mechanism 1 notes `obsolete` is *"the one sanctioned status on a documentation node (#493 §5)."* A superseded **task** cannot take it — the status slot is held by its lifecycle (`closed`) — and #7216 names no replacement. This is a **gap, not an error**: the document is silent on the task case rather than wrong about it. Measured consequence:

- Selecting rows to enrich by `status == "obsolete"` — a shape I costed and preferred, because it needs no verb vocabulary — **fires on 2 nodes out of 10,863 and misses every pair the task was written about.** It is dead on arrival, and only measurement says so.
- **The edge is the only mechanism that is present in all nine cases.** Whatever the selector is, it must key on the edge.

**The seventh pair was a mis-verbed edge, and it is the best argument in this document for §7.5's direction rule.**

> **CORRECTED 2026-09-08, after this measurement.** The edge is now stored `13281 --fixes--> 829`, repatched by the operator once this section surfaced it, and re-verified here via `divoid_get_links`. The observation below is kept **as observed** rather than silently updated — it is the evidence for the design decision that follows it, and a reader who meets only the corrected edge cannot reconstruct why §7.5 exists.

**As measured**, the stored edge was `13281 --supersedes--> 829`, which reads *"the open task #13281 supersedes the install document #829."* It did not. #13281 is an **open task** — *"Publish the reconciled install.md over DiVoid node #829"* — and #829 is the **documentation** node it will publish onto (`status` null, no banner). Publishing onto a node is not superseding it, and the arrow additionally pointed the opposite way from the reading #7217's own list implies. This is #7216's recorded gotcha — `patch_link` normalizes to the stored orientation and cannot flip it — surfacing in production data, and it is the first observed instance of it.

**Design consequence — and the repair does not weaken it.** The MCP must **not** interpret the edge and label a row stale. It surfaces `source_id`, `target_id` and `context`, and the model reads the sentence. Had this feature shipped with a selector that auto-decided obsolescence, it would have told every reader that #829 — the current install document — was superseded, on the authority of a hand-written label that was wrong for eight days. **Mechanised trust in a hand-written label is worse than no label**, because the tool's confidence is not evidence about the labeller's care. That this one edge was repaired changes nothing structural: the verbs are hand-written and the vocabulary is hybrid by design (#7216), so the next wrong one is a matter of time, and the design must survive it rather than assume it away.

### 4.6 Supersession chains

Walking `superseded-by` from all 9 seeds (2 obsolete-status docs + 7 named pairs), cycle-guarded: **every chain has depth 1.** No node's canonical successor has a successor of its own. This is §9.1's whole argument.

---

## 5. The decision: enrich on `context is not null`

**S2.** Not S1, and the reasoning is KISS, not generosity.

**S1 is more mechanism than S2, not less.** S2 is `if link.get("context")`. S1 is a stem list, a matcher, and a standing obligation on #7216's self-healing clause to update the list whenever an obsolescence verb is coined — which is *"conventions rely on every agent remembering"* reintroduced one level up, in the design that exists to delete it. #1220's RULING 2026-09-03 says it in the words that fit: **prefer the shape that needs no new vocabulary.**

**S1 is also measurably lossy at the edges it must not lose.** Substring `supersede` over the graph matches **118 edges across 56 distinct context strings**, of which only 63 are exactly `supersedes`. The other 55 are prose — and several are *negations or mentions*, not supersessions: `"extends this design to scalar fields; supersedes nothing in …"`, `"corrects the superseded implementer-opens-the-PR chain on …"`, `"extends, closing the hole named in its §10.1 (does not supersede…)"`. A stem match has false positives today and, because the vocabulary is explicitly hybrid and growing (#7216), false negatives tomorrow. Probes for the plausible future coinages measure `replace` 8 edges, `retire` 5, `obsolet` 0, `deprecat` 0 — none of them a supersession verb *yet*, which is exactly the state that makes an allow-list look safe right before it stops being.

**S2 needs no vocabulary at all, so MCP invariant 6 is satisfied by construction.** `divoid-mcp/CLAUDE.md` invariant 6 forbids the system layer from hard-coding client vocabulary. S1 would need an argument that a *projection* filter is not the *rejection* the invariant is aimed at — a defensible argument I would rather not have to make. S2 does not need it.

**And S2 is the set #7216's own convention already curates.** Its substance test: *"an edge carries context only where a real verb applies… a plain `None` edge already says 'related'."* The 6.20% of edges that carry a context are the ones a person deliberately labelled. The MCP is not deciding what is meaningful; it is projecting what the graph already marked as meaningful. That is also the closer reading of Toni's ask — *"the clear correct structure"*, not *"the supersession edges"*.

**Rejected, with numbers:**

| Alternative | Why not |
|---|---|
| **S3** — all edges default-on | 11.8×; one 10-row search measured 114,903 bytes. Hub nodes (#325 at 1,069 edges) appear in ordinary searches. |
| **S1** — supersede-stem allow-list | 1.11× vs 1.59× — **both on the same disclosed sample (§4.2), so the comparison is apples-to-apples and survives that sample being biased**; only the absolute figures move. The 0.48× gap is bought with a vocabulary lock, a maintenance clause, and a silent-miss failure mode. See above. |
| **`status == "obsolete"` trigger** | Falsified by §4.5: 2 nodes graph-wide, 0 of the 7 named pairs. |
| **Backend-side context filter** | §10. Cost is wire bytes; benefit is not model tokens; and it is a second parallel projection (#1136 §2 Form 2). |
| Keep opt-in, fix the Primer instead | The Primer rule already exists and was loaded during the session that ignored it dozens of times. That experiment has run. |

---

## 6. The honest cost statement

Stating this as a continuous cost rather than an obvious win, per the brief:

> **CORRECTED after QA #13312.** This section first derived its costs from the single 1.59× figure, which came from a sample biased toward this task's own subject matter. The derived costs were **overstated by roughly 2.5×**. They are restated below from the measured range (§4.2). The correction was made *because this is the section a future reader consults to reopen the decision* — a reopen-on-evidence section carrying inflated evidence is worse than one carrying none.

**S2 costs roughly +600 to +1,500 bytes per search — ~150 to ~370 tokens — forever, to deliver an obsolescence signal that fires on a small minority of rows.** Against this document's 2,481-byte baseline that is the 1.232×–1.59× range measured across three independent runs; **~750 B / ~190 tokens is the working figure**, from the middle of that range. A session running 50 searches spends roughly **7,500–18,500 tokens**, most plausibly around 9,500.

The rows that fire but are not obsolescence carry `implements` / `reviews` / `depends-on` / `subtask-of` — real information, not noise, but **not the thing #7217 asked for.** On the disclosed sample, obsolescence was **4 rows in 100** and labelled edges of any kind **34 in 100**, and the obsolescence-relevant share of the byte cost was **17.7%** — S1's delta (285 B) over S2's (1,606 B).

**A second sample now exists, so this is a measured range rather than a scoped point: 7.2%–17.7%.** QA measured S1's delta at 42 B against S2's 582 B on their own ten queries (#13313), published in that review and reproduced in §4.2. **17.7% is the upper end, from the supersession-rich sample**, exactly as predicted when it was still a single figure. It was deliberately **not** rescaled while only one run had measured it — rescaling a figure nobody else measured manufactures a measurement — and it is restated now only because a second measurement exists.

Three things make me recommend paying it anyway, and the operator should weigh them rather than take them:

1. **The cheaper targeted alternative was measured and is worse in kind, not just in coverage** (§5). The S1/S2 gap is **0.21×–0.48×** across the two disclosed samples (0.48× on mine — the decision-time figure — and 0.21× on QA's). Whichever end you take, it buys a vocabulary lock.
2. **The failure it prevents is measured, not predicted.** #7217's triage records one capability gap filed six times — twice asserting the absence of a feature that had shipped 8 and 17 days earlier — and one defect filed four times across four months. Each duplicate re-derived context a surfaced edge would have short-circuited.
3. **The absolute numbers are small.** ~1.3× on 2.5 KB is not ~1.3× on 100 KB. `divoid_list` — the bulk-payload path, where a multiplier would actually hurt — measures 1.07–1.16×.

**This trade was accepted knowingly, not overlooked.** The operator took the decision on 2026-09-08 **on the figures as they stood at decision time, before the §4.2 correction** — *“~370 tokens per search, forever, five sixths of which is not obsolescence”* — on the ground that paying it beats maintaining a vocabulary list. **Those two numbers are preserved deliberately: they are the record of what was actually agreed, not current estimates.** The corrected figures are the paragraph above (~190 tokens; share range below), and both are lower, so the decision holds a fortiori. It is written here in those words so a future reader can reopen it **on evidence** rather than rediscover the argument and assume nobody weighed it.

**What would change this recommendation:** a measured search or list call where the S2 delta exceeds ~25% of a payload that is itself over ~40 KB. The worst case I could construct is 8,635 bytes on a 10-row search. If someone finds a worse one, S1 is the fallback and this document's §5 is where to argue it.

---

## 7. The design

### 7.1 One shared selector, in the module that already owns edge shaping

`_link_details.py` is already the single home for the camelCase→snake_case row normalization used by `divoid_search`, `divoid_list` and `divoid_patch_link`. It gains one function that takes the raw `linkDetails` array and returns the normalized rows for edges carrying a `context`.

**DRY math, stated honestly:** the block is ~6 lines at 2 sites — `6 × 2 = 12`, **below** #1267's ~15–20 threshold. The extraction is therefore **not** justified by the threshold. It is justified because `_link_details.py` already exists for exactly this concern and because the two tools' selection semantics must not drift apart; putting the predicate in one place is what makes "labelled" mean the same thing on both surfaces. Duplicating it would be within the letter of #1267 and would create the drift risk this whole document is about.

### 7.2 `divoid_search`

| | today | after |
|---|---|---|
| `include_link_details` default | `False` | `False` — **unchanged** |
| `fields` sent when no flag set | *absent* | always sent, always including `linkDetails` |
| `link_details` on a row, flag off | absent | **the labelled edges** (omitted when empty) |
| `link_details` on a row, flag on | all edges | all edges — **unchanged** |

The flag's meaning shifts from *"include link details"* to *"widen from labelled edges to all edges"*. **The parameter is not renamed**: it is public MCP surface, the repo's own note says the surface evolves deliberately, and a rename is a breaking change bought for tidiness. The docstring and tool description carry the new meaning instead. This is a deliberate accepted wart, recorded here so a later reader does not "fix" it.

`base_fields` (`search.py:156`) becomes the projection the row builder actually reads — see §8.1.

### 7.3 `divoid_list`

Same selector, same shape. Two list-specific rules:

- **`linkDetails` is appended unconditionally, including when the caller passed an explicit `fields`.** `fields` selects *node* fields; `linkDetails` is a derived adjacency projection. Keeping them orthogonal is what preserves the guarantee — a caller who narrows `fields` to save payload does not thereby switch off staleness detection. There is deliberately **no opt-out knob** (#1136 §3: no named operator, no measured need; §4.3 puts the cost at 1.07–1.16×).
- **`id` is force-included whenever `linkDetails` is appended** — mandatory, see §8.2.

**Omit-if-empty:** a row with no labelled edges carries no `link_details` key at all. This follows Toni's own wire convention from #11419 — *"on wire null is omited to save payload size. null and absent is to be considered the same."* (spelling as in the source) — and since enrichment is now unconditional, absent is unambiguous: it means *this node has no labelled edges*, never *nobody asked*.

### 7.4 The two tools are not interchangeable — `divoid_search` rebuilds rows, `divoid_list` passes them through

**Added after QA #13312, whose two critical fails share this single root cause.** It is stated as design content rather than as an erratum, because a design that specifies both tools in one breath will keep producing this class until it names why they are not the same.

**It has since produced a third instance, and that is the evidence it is a generator rather than a narration.** QA round 2's CF-3 — the fabricated `ownerId` below — was found **after** this section was written, by re-measuring the pass-through tool directly, which is the move this section prescribes. It is **invisible from the search side by construction**: `divoid_search`'s row builder is a fixed allowlist that never emits `ownerId` at all, so no amount of care on the search side could have surfaced it. Two instances made the pattern; the third was predicted by it. This is also #8642's own failure mode one level up — *“the lesson had been applied at exactly the level it was learned, and not one level deeper.”*

| | `divoid_search` | `divoid_list` |
|---|---|---|
| Row construction | **rebuilt** from a fixed key set (`search.py:202-219`) | **passed through verbatim** from the backend (`list_nodes.py:313-323`, whose own comment calls it *“this otherwise pass-through endpoint”*) |
| Effect of a narrower `fields` | a key I control turns **`null`** — visible, and the defect §8.1 exists to fix | the key **disappears** — the row silently shrinks |
| Caught by a unit test? | yes, the row shape is asserted | **no** — every list test uses a synthetic payload, so the mock returns whatever the fixture declares and the projection narrowing is invisible by construction |

**The concrete consequence, measured twice — by QA live (#13312 CF-2) and independently here.** A **default** `divoid_list` call sends no `fields` param today, so the backend returns its 11-key default: `access, contentType, created, id, lastUpdate, name, ownerId, rootNodeId, severity, status, type`. Making the projection unconditional replaces that with whatever the MCP enumerates, and **how much is lost depends on whether §8.1's additions are mirrored onto `divoid_list`**:

| variant | node fields requested | lost from a default row |
|---|---|---|
| §8.1 **not** mirrored (bare `_DEFAULT_FIELDS`, `list_nodes.py:188`) | 5 | `severity`, `rootNodeId`, `access`, `created`, `lastUpdate` — QA's measured case |
| §8.1 mirrored (this design's intent, §7.3) | 7 | `access`, `created`, `lastUpdate` — measured here |

**Both variants also lose the true `ownerId`, and neither loses the key.** It returns as a fabricated **`0`** under the separate #11417 CLR-default defect — a value indistinguishable from a genuine *unowned* row. So the narrowed row drops real fields *and* gains a wrong one that looks entirely ordinary.

**This is the one loss no disclosure can repair, and it is why QA graded it a critical fail (#13313 CF-3).** The dropped fields announce their own absence; a fabricated `0` announces nothing. Measured over **2,000 rows** under the backend's default projection, `ownerId: 0` is **36.7%** of the graph — genuinely unowned rows — against `2` at 43.5%, `10` at 8.4% and a long tail. So better than a third of all rows legitimately read `0`, and after this change **every** row does.

> **One correction to the review's phrasing, which does not weaken it.** #13313 calls `0` *“the single most frequent correct value”*, measured over 60 rows where it led at 42%. Over 2,000 rows it is the **second** most frequent, behind owner `2`. The superlative does not survive the larger sample; **the argument does not depend on it** — a fabricated value colliding with 36.7% of real ones is no more detectable than one colliding with 43.5%. Recorded because a quantified superlative is exactly the kind of closing flourish that gets quoted onward after the claim it decorated has been forgotten. **The design's intent is the second row and the first is what shipped**, which is itself the point of this section: §8.1 was written for `search` and did not travel to `list`, because on `list` nobody was looking at a rebuilt row to notice.

**The second consequence is CF-1, and it is the cleaner illustration.** On `divoid_list` the normalization must `row.pop("linkDetails")`, not merely read it — because the row is passed through, a raw camelCase `linkDetails` left in place ships to the caller **alongside** the snake_case `link_details`, leaking the full unfiltered adjacency into every row and doubling the payload the selector just trimmed. **On `divoid_search` that failure cannot occur at all**: the rebuild copies named keys out, so a key nobody copies simply never appears. The `pop` is load-bearing on one tool and inert on the other, which is precisely why it reads as incidental when both are specified together.

**That is the identical defect class as §8.1, introduced on the sibling tool by the same diff** — and it is worse there, because on search the symptom is a `null` on a key someone declared, while on list the field is simply absent and nothing in the response says it was ever expected.

#### The rule for `divoid_list`

> **WITHDRAWN 2026-09-08 (QA #13313 CF-2). The first version of this rule read:** *“the projection the MCP sends must be a **superset of what a default call returns today** … Enumerate the backend's default set explicitly, add `linkDetails`, and force `id`”* — **and it was stated as “not optional”.** It is kept here rather than deleted because the implementer shipped a narrower set against it, and a reader who meets only the replacement cannot see why that happened.

**Why it was wrong — and the first reason is the one that matters, because it is not the reason the review gave.**

1. **It does not guard the defect it was written for.** Measured: `GET /api/nodes?id=7217&fields=id,name` returns **`ownerId: 0`** — a field the request never mentions. The fabrication is caused by `NodeDetails.OwnerId` being a **non-nullable `long`** (`NodeDetails.cs:109`), so the serializer cannot omit it and an unrun mapping leaves the CLR default (#11417). **The backend's default set has nothing to do with it.** Had the backend default never carried `ownerId`, the superset rule would have permitted dropping it and the fabricated `0` would have appeared anyway. The rule got CF-3 right **by coincidence** and is silent on its mechanism — which is the definition of a rule that will not transfer.
2. **It is not checkable without a live probe against a moving target.** *“What a default call returns today”* is not a property of the MCP source, and §7.4's own table says list tests use synthetic payloads and cannot observe a projection at all. So the only instrument is a live call against one backend build at one moment — **the round-1 §16 lesson in rule form**: a criterion defined by an undisclosed, moving input is not reproducible, and will be verified once and then silently rot.
3. **It welds the MCP to the backend default**, re-creating one layer up the coupling §7.4 exists to break (QA's argument, and correct). Per **#8642** — *“a rule stated as a syntactic pattern gets pattern-matched; a rule stated as a property gets reasoned about”* — the enumeration form invites exactly the pattern-matching that produced this dispute. (#8642's *name* is about guard falsifiability and would have led me to reject the citation; its body carries the principle. Resolved, not judged by name.)

**The replacement — two rules, and only the first is absolute.**

> **R1 (absolute, mechanically checkable).** A field may be omitted from the **default** projection **only if it is nullable on the wire type.** A non-nullable value member keeps its CLR default when its mapping does not run and the serializer cannot drop it, so omitting it does not narrow the row — it **fabricates a value**.
>
> **R2 (defeasible).** A **nullable** field may be dropped from the default projection only where **all three** hold: its absence is self-announcing, it has no named consumer on this tool's own surface, and the drop is disclosed in the tool description.

**R1's check is a read, not a probe.** List the non-nullable value members of `NodeDetails` and confirm each is in the projection. At `ab03c3a` there are **exactly two**: `long Id` (`NodeDetails.cs:13`) and `long OwnerId` (`:109`). Every other member is nullable (`int? Severity`, `long? RootNodeId`, `float? Similarity`, `double? X`/`Y`, `NodeAccess? Access`, `DateTime? Created`/`LastUpdate`) or a reference type, and absent-when-null is the backend's stated convention (#11419). **So `id` and `ownerId` are always requested, and nothing else is forced.**

**R1 is scoped to the default projection, matching R2** (#13315 W-8): a caller who passes an explicit `fields` can still omit `ownerId` and get the fabricated `0`, but that path already behaved that way at HEAD, is untouched by this change, and belongs to **#11417** — stating R1 unscoped would leave it reading as violated by shipped code, which is how a rule gets ignored or over-applied.

**“The wire type” means `NodeDetails`, and that is the whole surface R1 governs** (#13315 W-10): `linkDetails` also puts **`NodeLink`** on the wire, which has three non-nullable value members (`long SourceId`, `long TargetId`, `LinkType LinkType`) against one nullable reference member (`string Context`) — but **no fabrication can arise there, because `fields=` has no per-edge granularity**: `MaterializeAdjacency` assigns the whole `NodeLink[]` or none of it (`NodeService.cs:727-732`), so no edge member ever has an unrun mapping. The nullability split is still visible in the data exactly as predicted — `linkType` present on every edge, `context` on the small minority that carry one — and that is the rule confirming itself, not a hole in it.

**R1 subsumes §8.2's “force `id`”.** That was justified ad hoc — the adjacency lookup keys on `Id`. R1 derives it from the same property that derives `ownerId`, so two unrelated-looking rules collapse into one. #1136 §4's *can-it-be-merged* check answering yes.

**And R1 retires itself.** When #12952 makes `Id` and `OwnerId` `long?`, the set of non-nullable value members becomes empty and R1 forces nothing — without anyone editing this document. A rule that dissolves when its cause is fixed is the shape to prefer over one that must be re-verified on every backend change.

**Applying R2 to the disputed three:** `severity` and `rootNodeId` have **named consumers on this same tool** (live `severity` / `root_node_id` filters), so they stay. `access`, `created` and `lastUpdate` are nullable, announce their own absence, have no named consumer in the tool result, and the drop is disclosed — so dropping them is compliant. **Under R1+R2 the shipped set is compliant once `ownerId` is added**, which is CF-3's one-line fix.

#### On the bounce — and the larger half of it is mine

The implementer's half is the standard one: **a design rule believed wrong is bounced, not shipped as a narrower variant with a rationale attached** (#114 §0, #1136 §7). Disclosing the reasoning in the tool description was far better than silence and is not the mechanism for changing a rule.

**The larger half is the design's.** I wrote *“and it is not optional”* on a rule that was wrong in a way that made **complying with it produce a worse result** — four fields nobody consumes, restored to satisfy a criterion that does not guard what it claims to. That does not merely permit the non-bounce; it **manufactures** the dilemma, leaving the implementer to choose between shipping something worse and stalling on the architect's availability.

> **An architect may phrase a rule as binding only when it is property-shaped and checkable without a live probe.** That is the only kind of rule an implementer can both comply with and *confirm* they complied with, unaided. R1 is checkable by reading one file; R2 is a three-clause test on facts the implementer already holds. Both are shaped so the next implementer can comply rather than adjudicate.

**And the reasoning rule behind it:** every step of §7 and §8 was reasoned on `divoid_search`, where the row is rebuilt, and then carried to `divoid_list`, where it is not. **A conclusion about a rebuilt row does not transfer to a passed-through one** — rebuilding *hides* a projection change behind a null, pass-through *exposes* it as a missing key. Any future change touching both tools must be re-derived on the pass-through side rather than assumed from the search side, and the absence of a failing test is not evidence, because synthetic payloads cannot observe this at all.

### 7.5 What the tool description must say

The description is the only channel by which a model learns the new default. It must state, in the returned-shape paragraph, that (a) labelled edges arrive on every row without a flag, (b) an edge whose `context` reads `supersedes` and whose **`target_id` is this row** means this row is the stale one and `source_id` is where to go, and (c) `include_link_details=True` widens to all edges. Point (b) is the sentence that turns data into behaviour; without it the row is decoration.

---

## 8. Two fixes that default-on forces

Neither is optional. Both are latent today and become universal the moment the enrichment is unconditional.

### 8.1 `include_link_details=True` returns `severity` and `rootNodeId` as `null`

`search.py:156` requests six fields; the row builder at `search.py:202-219` reads **eight**, including `severity` (`:207`) and `rootNodeId` (`:209`). Neither is in the projection, so both arrive absent and are emitted as `null`.

Measured on #7217, whose real values are `severity: 3`, `rootNodeId: 3`:

| call | `severity` | `rootNodeId` |
|---|---|---|
| `divoid_search(...)` (no flags) | `3` | `3` |
| `divoid_search(..., include_link_details=True)` | **`null`** | **`null`** |

This is a **silent wrong value, not an omission** — the same failure class as #11417, one layer up and MCP-side. **Its sibling on `divoid_list` is §7.4**, where the same projection narrowing deletes keys outright instead of nulling them; fix both or neither. `severity` is how a task's urgency is read and `rootNodeId` is how #6857's structural home is read; both silently vanish for anyone using the flag today.

**Fix:** add `severity` and `rootNodeId` to `base_fields`. Verified the backend accepts both field names and returns the true values (HTTP 200, `severity: 3`, `rootNodeId: 3`). One line, no backend change.

I checked for a pre-existing filing before treating this as new: #1611 (*add severity to wire contract*) is **closed** and is about adding the field, not about the projection dropping it; #11417 / #12953 / #12952 are the backend CLR-default family and do not cover this. Because default-on makes it universal, it ships **in this PR** rather than as a separate task — filing it separately would recreate the open-and-unlinked pattern §6 point 2 cites.

### 8.2 Appending `linkDetails` without `id` silently returns zero edges

Bug **#12953**, open, severity 2. `NodeService.MaterializeAdjacency` keys its adjacency dictionary on the DTO's `Id`; `NodeDetails.Id` is a non-nullable `long`, so when the `id` mapping does not run it holds `0`, every lookup misses, and every row falls to the `: []` branch.

Reproduced against production today on node #190:

| request | `id` returned | `len(linkDetails)` |
|---|---|---|
| `?id=190&fields=id,name,linkDetails` | 190 | **73** |
| `?id=190&fields=name,linkDetails` | 0 | **0** |

`divoid_search` is safe by luck — `search.py:156` already begins with `"id"`. **`divoid_list` is not**: `list_nodes.py:232` builds from the caller's `fields` verbatim, so `divoid_list(fields=["name"])` under default-on would append `linkDetails` and return **`link_details: []` on every row**. That is a plausible, undetectable answer — *this node has no labelled edges* — and it is precisely the silent-staleness failure this document exists to remove, recreated by the feature meant to remove it.

**Fix:** the MCP force-includes `id` whenever it appends `links` or `linkDetails`, mirroring the backend's own `contentType`-when-`content` rule (`NodeService.cs:773-774`). This is a **workaround, not the fix** — the fix is #12952 §6/§15 making `NodeDetails.Id` a `long?`. The two do not conflict: when #12952 lands, the MCP's forced `id` becomes redundant and harmless. **Do not resolve this by any variant of `?? 0`** — #12953 names that explicitly as reproducing the defect behind something that looks like a fix.

---

## 9. #7217 items 2 and 3 — recommended not built

### 9.1 `resolve_canonical` — no

**Measured chain depth is 1** across all 9 supersession seeds in the graph (§4.6). A tool whose job is walking a chain to its tip would, on today's data, always return the node the enriched search row already handed the agent one step earlier. That is #1136 §4's *can-it-be-deleted* check answering yes.

**And a longer chain unwinds on its own.** If #A is superseded by #B and #B later by #C, the row for #A carries the #B edge; the agent's next act — a search or `get_node` on #B, per #7216's search-first reflex — carries the #C edge. One hop per read, no tool, no cycle guard, no new surface.

**The sign-off question, answered because the brief asks it.** `divoid-mcp/CLAUDE.md`: *"New tools require human sign-off from the repo owner before implementation — this is a generic-purpose tool used outside this deployment, so the surface evolves deliberately."* The clause is written about **tools**, so a new `divoid_resolve_canonical` engages it and a flag on an existing tool does not. That is the plain reading and it is the one I apply. I note, without stretching it, that the *rationale* — a generic tool used outside this deployment — would bear on a flag too, because a `supersedes`-walking flag bakes this deployment's client vocabulary into a generic surface. **Since I recommend building neither, this design requires no sign-off.** If the operator wants it anyway, it is a new tool and the clause binds.

**What would change this:** any supersession chain of depth ≥2 appearing in the graph. The check is the §16 command, and it is cheap enough to re-run rather than assume.

### 9.2 `get_content` / `get_node` enrichment — no

The refinement demoted this to bootstrap-only *because search is the entry point*. That premise still holds, and two measured facts make the remaining gap smaller than the task assumed:

- **`get_node` already returns `status`** (`get_node.py:76`). It is not blind.
- **`get_content` gets the banner.** #7216 mechanism 3 mandates it as the body's first lines, and it is present on **2/2** of the graph's `status=obsolete` nodes (§4.5).

There is a real hole and it is worth naming rather than papering over: **the banner is present on 0/7 of the superseded tasks**, and a superseded task carries `closed`, not `obsolete`. So for tasks, `get_content` genuinely announces nothing. But the remedy for that is not a fourth read surface — it is that a task's obsolescence lives in its status and its edge, and after this change **both are on the search row**. Adding enrichment to `get_content` would buy the narrow case of an agent that reached a task body by a hardcoded id — which #7216 and #190 Rule 1 already forbid.

**Recommendation: drop item 3 from #7217.** Not deferred, dropped. It has been open since July and the surface it was written for is now covered.

**Ruled 2026-09-08: dropped.** Why dropped rather than deferred is worth stating on the task itself, because **a task describing work nobody will do is not free.** #7217's own triage measures that cost twice: an open task describing a resolved or non-existent problem reads as *corroboration* to whoever finds it next — which is how one capability gap came to be filed six times and one defect four. Deferring item 3 would have left precisely that artefact. It is recorded as a decision with its reason — *search is the entry point* — so the next reader meets a judgement rather than a gap.

---

## 10. Where each change lives

**100% `divoid-mcp/`. Zero `Backend/`.** The brief asked me to establish this rather than assume it; here is the establishment.

The backend already serves everything needed. `?fields=linkDetails` is a first-class projection (`NodeService.cs:778`), materialized by `MaterializeAdjacency` (`:707`) from **one batched secondary query** over the whole page (`FetchAdjacentEdges`, `:675`) — never per row. `LinkType` and `Context` are already selected (`:682`) and already reach the wire (`NodeDetails.LinkDetails`, `NodeDetails.cs:104`). Measured latency cost of switching it on: **+121 ms** (§4.4).

**The case for a backend-side context filter, and why it loses.** Default-on means every search pulls all incident edges over the wire — 114,903 bytes and 1,882 edge rows in the worst case measured — so the MCP discards most of them (graph-wide, 93.80% of edges carry no context — §4.1). Narrowing server-side (`WHERE Context IS NOT NULL`) would cut both. It loses on three counts:

1. **The cost it saves is not the cost that matters.** Wire bytes and DB rows do not enter a context window. The model-visible payload is identical either way; only §4.4's 121 ms is real, on an already-1.4-second query.
2. **It cannot reuse `?fields=linkDetails`** without changing what that field means for the frontend viewport and every existing caller. It needs a *second* field name — a parallel projection, #1136 §2 Form 2.
3. **It puts client vocabulary in the system layer.** Even filtering on *"has a context"* is a policy about what the client finds meaningful, and the backend is the wrong place for it.

**Falsifier for this section:** if search p50 latency regresses by more than ~200 ms after the change, or the hub-node edge fetch shows up as database load, the escalation is a distinctly-named backend projection — not a change to `?fields=linkDetails`.

---

## 11. Documentation deltas (required, not optional)

#7217 names these and they are what stop the convention layer contradicting the tooling. They are DiVoid nodes, so the operator publishes them; this design specifies the content.

| Node | Change |
|---|---|
| **#7216** | *"Load link details when you read"* → the tooling does it. **Retract in place, do not delete** (#11228 Lesson 3): mark the manual reflex `WITHDRAWN 2026-09-08`, state that search/list now carry labelled edges by default, and keep the sentence so a reader who already acted on it learns it was retracted. |
| **#7216** | Close the §4.5 gap: *"use ALL THREE"* is unqualified, but mechanism 1 is documentation-only by its own parenthetical, and nothing says what a **task** does instead. State it — a superseded task takes `closed` and carries the edge; the **edge is the only mechanism present in all nine observed cases**, and anyone relying on `status=obsolete` to find stale work is relying on 2 nodes out of 10,863. |
| **#7216** | Record the #13281 → #829 case (§4.5) against the `patch_link`-cannot-flip gotcha the document already carries. The gotcha had **no observed example**; this is one. The edge itself is already repaired (`fixes`, 2026-09-08), so what is owed is the **record**, not the fix — and worth stating with it: the edge stood wrong for eight days and read as authoritative throughout. |
| **#190 / #9 / Primer Rule 10** | Relax *"pass the include-link-details flag"* to *"the tooling surfaces it; verify with `include_link_details=True` if in doubt."* Primer Rule 10's flag instruction becomes wrong once this ships. |
| **`divoid-mcp/CLAUDE.md`** | Repo-layout says `tests/smoke/ # live integration scripts (not pytest)` and does not mention `tests/unit/` — **25 pytest modules** (`ls tests/unit/test_*.py | wc -l`). Stale; incidental to this work, one line to fix, in scope for this PR since the PR touches two of those modules. |

---

## 12. Coverage

Per #1220: **every row names the guard, not the mechanism.** The three existing tests named below were read at `ab03c3a`; the new ones do not exist yet and are specified here.

**The falsifier column names the mutation the implementer must observe going red. I have run none of them** — I have no harness and it is not my artifact. Per #1220's §5 addendum of 2026-09-05, an unrun prediction may not be written as if measured, so the column is labelled as a specification, not a result.

| # | Property | Guard | Mutation the implementer must watch go red (**unrun**) |
|---|---|---|---|
| 1 | A default search sends `linkDetails` in `fields` | **rewrite** `test_search.py:80 test_flag_off_no_field_param_and_no_output_key` → `test_default_call_requests_link_details`. It must assert **positively** — `"linkDetails" in sent_fields` — not the negation of the old assertion. A `"fields" not in ...`-shaped assertion inverted into `assert "x" not in y` on a key that was never present is vacuous and passes against anything. | revert `base_fields` to the six-field list |
| 2 | A default search row carries labelled edges | new `test_default_row_carries_labelled_edges` | make the selector return `[]` |
| 3 | A default search row **omits** unlabelled edges | new `test_default_row_omits_unlabelled_edges` | change the predicate to `True` |
| 4 | `include_link_details=True` still returns **all** edges | **CORRECTED — new** `test_flag_on_returns_unlabelled_edges_too`, whose fixture must carry **one labelled and one unlabelled** edge and assert **both** survive. The originally-named `test_flag_on_appends_field_and_normalizes_output` does **not** constrain this property: at `ab03c3a` its fixture holds a single edge carrying `context: "subtask"`, so the labelled-only selector leaves its result identical and it passes against the broken implementation. | apply the labelled-only selector on the flag-on path too. **Until the named guard exists, this mutation still dies** — on `test_flag_on_missing_link_type_context_not_fabricated` and `test_composes_with_include_links`, whose names claim other properties (#13313 W-6). A property constrained under a misleading name is a documentation defect, not a coverage hole; contrast row 10, where the mutation killed nothing. |
| 5 | A row with no labelled edges omits the key entirely | new `test_row_without_labelled_edges_omits_key` | emit `[]` unconditionally |
| 6 | `severity` and `rootNodeId` survive the flag-on path (§8.1) | new `test_link_details_projection_keeps_severity_and_root_node_id` | remove either name from `base_fields`; the row goes `null` while the call still succeeds |
| 7 | `divoid_list` force-includes `id` when appending `linkDetails` (§8.2) | new `test_list_forces_id_when_appending_link_details` | drop the force-include; assert on the **sent `fields` param**, not the response — a mock returns edges regardless |
| 8 | `link_type` / `context` are still never fabricated | existing `test_search.py:157 test_flag_on_missing_link_type_context_not_fabricated` | have the selector default `context` to `""` |
| 9 | `include_links` still composes | existing `test_search.py:185 test_composes_with_include_links` | make the selector drop the `links` key |
| 10 | **ADDED after QA #13312 (CF-1).** A `divoid_list` row never carries the raw camelCase `linkDetails` — on **either** path | new `test_list_row_never_carries_raw_link_details_key`, asserting `"linkDetails" not in row` **and** `link_details` present with the expected contents, on the flag-off path | change `row.pop("linkDetails")` to `row["linkDetails"]`. QA measured this mutation killing **zero** tests on the shipped diff |

**Falsifier over the table itself** (#1220): *any row whose named guard would still pass against an implementation lacking the claimed property.*

> **CORRECTED after QA #13312 — I ran this falsifier and it missed a row.** I flagged **row 7** as the table's risk and row 7 held up under review. **Row 4 is the row that actually failed the table's own falsifier**, and I walked past it. The mechanism is #1220's: *a verification pass finds the defect shape it is hunting.* I was hunting guards that **fire on compliant code** — row 7's mock problem — and was blind to a guard that was **inert**, whose fixture simply could not tell the two implementations apart. The two failure shapes need two passes, or a fixture-level check: **for every row, ask what the fixture would have to contain for the mutation to change its result, and confirm it contains that.** Row 4's needed an unlabelled edge and had none.

**Row 10 did not exist until QA found it, and the reason is the same asymmetry §7.4 describes.** The property *“the raw key does not leak”* is unfalsifiable on `divoid_search` — the rebuild makes it true by construction — so writing the table with search in mind produced no row for it. A guard that is vacuous on one tool and load-bearing on the other will be omitted by anyone reasoning from the tool where it is vacuous. **When two tools share a specification, derive the coverage table on the weaker one.**

Row 7 remains the one that fails it if written carelessly — a unit test that mocks the HTTP response will return edges whether or not `id` was sent, so the assertion **must** be on the outgoing request's `fields` parameter. The existing tests capture request params (`captured[0].url.params`, `test_search.py:99`), so the instrument is already in the suite.

**Row 1 is not a prediction.** `test_flag_off_no_field_param_and_no_output_key` asserts `"fields" not in sent_params` (`:99`) and `"link_details" not in row` (`:102`). This change makes both statements false, which is decidable by reading the file. Its docstring at `:83` already names the substitution — *"if include_link_details defaulted to True … this test fails in that case"* — so the test was written as the guard for exactly the decision now being reversed. **It must be rewritten, not deleted**, and its replacement must assert the new default just as sharply.

---

## 13. Pre-Design Checklist (#1136 §5)

**KISS / DRY / YAGNI**
- No new type, no new abstraction, no mirror shape. The change is one predicate and one field-list edit.
- No element justified by "we might need X later"; the two rejected items (§9) are rejected on measurement, not deferred.
- No deprecation period, feature flag, compatibility shim or transition window. The default flips in one commit.
- DRY math stated and **stated as not meeting the threshold**: `6 lines × 2 sites = 12`, under ~15–20. The extraction is justified by the existing module's ownership and by drift risk, not by #1267. §7.1.

**Existing systems first**
- The backend projection, the batched adjacency query and the normalization module all already exist; §10 audits each and adds none.
- No new persisted data point. No new layer.
- Consumer chain recursed: the enriched edge is read by the model, which acts on it by opening the canonical node. The consumer is named and is the point of the feature.

**Configurability**
- No new config knob. The deliberate absence of an opt-out is argued in §7.3 with the measured cost that makes it affordable.
- No magic numbers introduced.

**Less is better**
- Delete / merge / inline run on every element: the selector cannot be deleted (it is the feature), cannot be inlined without the §7.1 drift risk, and is merged into the module that already owns edge shaping.
- Trade-offs named explicitly in §5 and §6, including the one against the recommendation.
- No compromise shape: the choice is between three concrete selectors, all costed, one picked.

**Document discipline**
- #114 and #1136 cited as load-bearing. Out-of-scope items enumerated in §2 rather than left absent.
- No multi-paragraph rationale for things that obviously stay.
- No predecessor design is superseded by this one, so no `SUPERSEDED` banner is owed. `docs/architecture/fields-projection-nullability.md` (#12952) is **adjacent and stays canonical**; §8.2 is a workaround under it, not a replacement for it.

---

## 14. Risks

| Risk | Severity | Mitigation |
|---|---|---|
| An agent sees a `supersedes` edge and misreads the direction, treating the canonical node as stale | medium | The tool description must state the rule explicitly (§7.5): `target_id == this row` means *this row is stale*. #7216 records that `patch_link` cannot flip a stored orientation, so mis-oriented edges exist in the data and the model must read the sentence, not assume. **Observed, not hypothesised:** the #13281 → #829 edge carried the wrong verb for eight days (§4.5; repaired 2026-09-08). Repairing one instance does not retire the risk — the verbs are hand-written and the vocabulary is hybrid by design. |
| Free-prose contexts inflate rows unpredictably | low | Measured, not modelled. Context length over all 2,014 contexted edges: median **25**, p90 **72**, p99 **144**, **max 251** chars. Worst 10-row search 8,635 bytes; worst list row 10 edges. Already inside every figure in §4. |
| A caller relying on today's `link_details`-absent-when-flag-off behaviour breaks | low | Callers are agents reading a tool result, not parsers. `include_link_details=True` semantics are unchanged, which is the only contract anything could be built on. |
| #12952 lands and the forced `id` becomes dead weight | low | It is one list entry, harmless after the backend fix, and removing it is a one-line follow-up. Named here so it is not mistaken for load-bearing later. |
| A future obsolescence verb is coined and not surfaced | **eliminated** | This is S1's risk and is the reason S2 was chosen. S2 surfaces any labelled edge regardless of verb. |

---

## 15. Implementation order

1. **`_link_details.py`** — add the labelled-edge selector beside `normalize_link_details`. No caller changes yet; the suite stays green.
2. **`search.py`** — add `severity` and `rootNodeId` to `base_fields` (§8.1), make the `fields` projection unconditional, route the flag-off path through the selector, omit the key when empty.
3. **`list_nodes.py`** — same, plus the forced `id` (§8.2), **and §7.4’s superset rule**: a default call must send a projection at least as wide as the backend’s 11-key default, not the MCP’s narrower `_DEFAULT_FIELDS`. `fields` is appended-to, never replaced. **No unit test can catch a mistake here** (§7.4) — verify against the live API, not the suite.
4. **Tool descriptions** on both tools (§7.5) — including the direction rule.
5. **Tests** — rewrite guard 1 (positive assertion, not an inverted negative); **add guards 2, 3, 4, 5, 6, 7, 10**; confirm 8 and 9 still pass unchanged. **Guard 4 is a new test, not the existing one** — the originally-named guard cannot discriminate (§12). **Guard 10 is the CF-1 guard** and is the only one that catches the raw-key leak on `divoid_list`.
6. **`divoid-mcp/CLAUDE.md`** — the stale repo-layout line (§11).
7. Operator publishes the #7216 / #190 / #9 / Primer deltas (§11).

Steps 1–6 are one PR. There is one feature here, and steps 2 and 3 cannot ship apart from step 1.

---

## 16. Commands

Every figure in this document came from one of these, run against production on 2026-09-08 from `main` @ `ab03c3a`.

The three `measure_*.py` scripts below were written to session scratch (`C:\dev\claude\_scratch\obsolescence7217-k4m\`) and **should be assumed gone** — that path is a shared root nobody preserves. All three are stdlib-only (`urllib`), read the credentials from `~/.claude/secrets/.divoid-online`, and touch nothing.

> **CORRECTED after QA #13312.** This paragraph claimed the method being stated here made *“every figure re-derivable without them”*. **That is false for any figure computed over a sample**, and §4.2's selector figures are exactly that. A method is re-derivable; **a measurement over an undisclosed sample is not** — which is why three competent runs of the same method produced 1.232×, 1.30× and 1.59× and looked like a contradiction. The ten queries are now disclosed in §4.2, so those figures are reproducible; the graph-census and compliance figures never depended on a sample and always were.

The rule this leaves behind, which is the general one: **state the input with the measurement, or report it as a range.** Naming the tool and the method looks like sufficient provenance and is not.

- **`measure_verbs.py`** — page every node id via `?fields=id&count=500` (**cursor is `continue`, not `offset`** — see the trap below), then `GET /nodes/links?ids=<batch of 250>` over all of them, de-duplicating edges on `(sourceId, targetId)`. Counts `context` presence, `linkType`, context-string length, and degree. Ends with the §4.4 latency runs.
- **`measure_selectors.py`** — for each of 10 queries, one `GET /nodes?query=…&count=10&fields=id,type,name,status,contentType,similarity,linkDetails`; rebuild each row exactly as `search.py:202-219` does; apply each of the four selectors; `len(json.dumps(...).encode())` is the reported byte figure.
- **`measure_compliance.py`** — list `?status=obsolete`; for each, and for the 7 pairs #7217 names, fetch incident edges and the first 400 bytes of content, and record status / inbound `supersedes` / banner. Then walk `superseded-by` from every seed with a visited-set cycle guard for §4.6.

```bash
DIVOID_URL=$(awk -F= '/^Url=/{print $2}' "$HOME/.claude/secrets/.divoid-online")
DIVOID_KEY=$(awk -F= '/^ApiKey=/{print $2}' "$HOME/.claude/secrets/.divoid-online")

# §3 — the four claims
git diff --stat 344f835..HEAD
grep -rn "resolve_canonical\|resolveCanonical" --include=*.py --include=*.cs --include=*.md .
sed -n '73p;156p;207p;209p' divoid-mcp/src/divoid_mcp/tools/search.py
sed -n '188p;232p;354p' divoid-mcp/src/divoid_mcp/tools/list_nodes.py

# §4.1 census · §4.2/4.3 payload · §4.4 latency · §4.5/4.6 compliance
python measure_verbs.py       # graph-wide edge + context census, degree, latency
python measure_selectors.py   # S0/S1/S2/S3 emitted bytes over 10 queries x 10 rows
python measure_compliance.py  # status/edge/banner per node; chain depth

# §8.1 — severity/rootNodeId nulled by the flag-on projection
curl -s -H "Authorization: Bearer $DIVOID_KEY" \
  "$DIVOID_URL/nodes?query=obsolescence%20protocol&count=1&fields=id&fields=type&fields=name&fields=status&fields=contentType&fields=similarity&fields=linkDetails"
curl -s -H "Authorization: Bearer $DIVOID_KEY" \
  "$DIVOID_URL/nodes?query=obsolescence%20protocol&count=1&fields=id&fields=type&fields=name&fields=status&fields=severity&fields=rootNodeId&fields=contentType&fields=similarity&fields=linkDetails"

# §8.2 — the missing-id adjacency trap, reproduced on #190
curl -s -H "Authorization: Bearer $DIVOID_KEY" "$DIVOID_URL/nodes?id=190&fields=id,name,linkDetails"
curl -s -H "Authorization: Bearer $DIVOID_KEY" "$DIVOID_URL/nodes?id=190&fields=name,linkDetails"
```

**Trap for anyone re-running these:** `/api/nodes` **silently ignores `?offset=`**; the cursor is `?continue=`. A paging loop keyed on `offset` returns page 1 forever and never terminates. Cost me one hung sweep.

Filed as **#13308** (bug, severity 2) after the operator searched for a prior filing and found none. It is filed as the **class, not the instance**: on this API, *passing a parameter that does not exist is indistinguishable from passing one that does.* **#11262** already measured the same class on a different axis: *every wrong spelling of a **scope** parameter fails open to a full-graph ranking* (`rootnode=`, `root_node_id=` → all 10,396 nodes; only `rootNodeId=` parses). Mine is the **paging** axis, and it fails open to page one. Scope fails open to everything, paging fails open to the first slice — one over-reports, the other under-reports, and neither says a word. Whether the remedy is per-parameter or rejecting unknown query parameters wholesale is a design question, and #13308 leaves it open rather than prescribing one.

**The shape worth carrying:** the loop that hangs is the **lucky** failure. The dangerous one *terminates* — three retrievals of page one, deduplicated by id, reported as a confident undercount. Nothing in that result looks wrong.

---

## 17. Decisions taken

All three questions this document raised were answered by the operator on 2026-09-08, before implementation. **They are recorded as decisions rather than left standing as questions** — an open question that has already been answered reads as live to whoever finds it next, which is the artefact §6 point 2 measures the cost of.

| # | Question | Ruling | Reopen on |
|---|---|---|---|
| 1 | Buy the 30-in-100 rows of non-obsolescence labelled edges, or take S1 at 1.11×? | **S2 — buy them.** Decided on the KISS argument rather than the coverage: the stem filter is *more* mechanism and silently misses the next coined verb, so it is not a cheaper version of this fix but a different bug. The reading of *"the clear correct structure"* as broader than supersession was confirmed. | §6's tripwire — a measured call where the S2 delta exceeds ~25% of a payload already past ~40 KB. |
| 2 | Drop item 3 from #7217, or leave it open? | **Dropped, not deferred** (§9.2). Reason of record: search is the entry point, `get_node` already returns `status`, and `get_content` gets the banner. | A read path that reaches a body without passing through a search or listing. |
| 3 | File the `?offset=` silent-ignore? | **Filed as #13308**, severity 2, as the class rather than the instance (§16). | — |

**Nothing in this document is waiting on an answer.** What remains is implementation (§15) and the documentation deltas (§11).
