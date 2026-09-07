# Architectural Document: File-path content input for the divoid-mcp composite create tools

**Status:** proposed · **Date:** 2026-09-07 · **Author:** Sarah (software architect)
**Source task:** DiVoid #7739 (post-triage scope) · **Duplicate consolidated:** #9876
**Repo:** `C:\dev\claude\divoid`, subtree `divoid-mcp/` · **Measured at:** `f8991d2`
**Load-bearing contracts:** Code Contracts #114 · Design Contracts #1136 · divoid-mcp CLAUDE.md · Tool anatomy #6104
**Decisions taken 2026-09-07:** all five open questions answered — see §15. Nothing was redesigned; §7.5 and R5
were added, and two parked findings were filed as #13217 and #13219.

---

## TL;DR

**What.** Give `divoid_create_documentation`, `_task`, `_session_log` and `_node` an optional `path`
parameter — the same name and semantics `divoid_set_content` already carries — so a document on disk
is uploaded rather than retyped through the model.

**How.** Extract the conflict guard and the gate-plus-read block that `set_content` already owns into
one shared `tools/_content.py`; the four creators call it. The file is read *before* the node POST,
so a bad path creates no orphan.

**Cost.** One new ~70-line helper, ~40 lines removed from `set_content`, ~20 added per creator,
~24 tests. No new tool, no new error code, no breaking change.

**Strongest rejected alternative.** Keep the two-step and document it better — rejected because its
only form is *create a documentation node with a placeholder body*, which is exactly what these
tools' content-required invariant forbids.

---

## 1. Problem Statement

An agent that has just written a document to disk and wants it in the graph cannot say so in one call.
The four composite creators accept a body only as an inline string, so the body must be re-emitted
verbatim through the model's output channel.

Per #9876, quoted into #7739, this is **not a size problem — it is a fidelity problem**:

> *"the tokens are affordable… verbatim re-emission of a 618-line document with tables, box-drawing
> diagrams, em-dashes and backtick-quoted paths is exactly the operation an LLM cannot guarantee.
> A silently dropped table row or a normalised dash produces a node that looks like the repo file and
> is not."*

There is a second harm the filings did not name, and it is the stronger one. The documented workaround
is: create the node with a placeholder to obtain atomic Docs-group resolution and `extra_links`, then
upload the bytes separately. `divoid_create_documentation`'s own invariant message reads:

> *"Documentation content must be non-empty and non-whitespace (per #493 §4)… **Do not create the node
> until you have the document.**"*

**The only available workflow requires violating the invariant the tool enforces.** The placeholder is
a lie, it is visible to every peer in the window between the two calls, and if the second call fails it
is a permanent lie with nothing to detect it. #9876 records itself doing exactly this — *"a short honest
placeholder"* — because the tool left no honest option.

**Success criteria.** A single call creates the node, resolves its group, attaches `extra_links`, and
sets a body that is byte-identical to a named file; a path that cannot be used produces no node at all.

---

## 2. Scope & Non-Scope

### In scope

| # | Item |
|---|---|
| 1 | An optional `path` parameter on `divoid_create_documentation`, `divoid_create_task`, `divoid_create_session_log`, `divoid_create_node` |
| 2 | One shared helper module under `tools/` owning the conflict guard and the gate-plus-read |
| 3 | Re-pointing `divoid_set_content` at that helper (it is the fifth caller, and the source of the logic) |
| 4 | The composition rule between each creator's existing content-required invariant and `path` |
| 5 | Tool-description text for the four creators, including the mandatory sensitive-path sentence (#6104 checklist step 6) |
| 6 | Unit coverage for the helper and for each creator's new branch |

### Explicitly out of scope

| # | Item | Why |
|---|---|---|
| 1 | A new `divoid_upload_content` tool | Rejected by #9876 itself and by the #8528 precedent; needs sign-off; does not give atomic create |
| 2 | A `content_type` parameter on the creators | No filing asks for it; the four creators post markdown by construction. YAGNI (#1136 §3). Named in Open Questions |
| 3 | Streaming / chunked upload | #8528 already ruled this YAGNI; no failing size has been observed. Unchanged here |
| 4 | Changing any existing error code, message, or default | This design is purely additive; see §12 |
| 5 | Extracting a module-level `_execute` from the three creators that lack one | Not required — see §5.3. A large mechanical refactor unrelated to this feature |
| 6 | `create_task`'s `_ALLOWED_STATUSES` allow-list — the same client-vocabulary allow-list PR #157 removed from `set_status` (invariant 6) | Pre-existing, and a different concern. **Filed as #13219**, before this PR exists, per #10895 §5.1 |
| 7 | Widening or narrowing the `paths.py` gate or its sensitive-name table | The gate is inherited unchanged; there is deliberately no opt-out |
| 8 | Sharpening the CLAUDE.md sign-off sentence (§12) | Governance in a feature PR makes the review answer two questions at once. **Filed as #13217** |
| 9 | Triage of #8440 and #11388 | Being handled by the coordinator. §7.5 carries the design-side remedy the triage identified |

---

## 3. Assumptions & Constraints

| # | Assumption / constraint | Basis | Confidence |
|---|---|---|---|
| A1 | The gate, the containment roots and the sensitive-name refusal already work and need no change | `paths.py` read at `f8991d2`; `tests/unit/test_paths.py` present | High — read |
| A2 | `DIVOID_MCP_FILE_ROOT` is configured usably on this machine | #7739 records a 100 KB design filed via `set_content(path=…)` on 2026-09-07 and verified byte-identical by `divoid_download_content` + sha256 | High — but see R5 |
| A3 | `path` is the established parameter name for a filesystem path in this server | `divoid_download_content(node_id, path)` `:168`; `divoid_set_content(id, content, path, content_type)` `:201-205` | High — read |
| A4 | FastMCP enforces nothing beyond coarse types; every real constraint is hand-written | #6104 §1, restated in all four creators' own docstrings | High |
| A5 | A partial failure after node creation is not rolled back | Architecture §6.3; each creator's `partial_state` branch | High — read |
| C1 | The repo is a pure client wrapper; no backend change is available or wanted | divoid-mcp CLAUDE.md §"What this repo is" | Hard constraint |
| C2 | Content must reach the wire as bytes, never as a string handed to a `data=`/`json=` kwarg (#187) | Invariant 4 | Hard constraint |
| C3 | Invariant guards run before any HTTP call | Invariant 5 | Hard constraint |
| C4 | The gate must be welded to the `open`, and the *resolved* path is the only one that may be opened | #6104 §3b | Hard constraint |
| C5 | No retries | Invariant 3 | Hard constraint |
| C6 | The MCP must not police client vocabulary | Invariant 6 | Not engaged by this design |

---

## 4. Architectural Overview

The machinery this task asks for exists. It sits inside one function in one tool. The design is a
**relocation plus four call sites** — not a new mechanism.

```
                    BEFORE (f8991d2)                          AFTER

  set_content._execute                        tools/_content.py
  ├── paths.gate(path)      ◄── the only      ├── guard_exclusive(content, path)
  ├── open(resolved, "rb")      copy of       │     └── raises content_path_conflict
  ├── file_not_found /           this         └── resolve_body(content, path, label)
  │   file_read_failed /        logic               ├── paths.gate(path)   ─┐ welded:
  │   file_empty                                    ├── open(resolved,"rb")┘ one function
  └── post_bytes                                    ├── file_not_found /
                                                    │   file_read_failed /
  create_documentation ─┐                           │   file_empty
  create_task           ├── content: str only       └── returns bytes | None
  create_session_log    │   ── no path ──                   or a refusal
  create_node          ─┘                                        ▲
                                                                 │
                                              ┌──────────────────┴──────────────────┐
                                              │                                     │
                                    set_content._execute              create_documentation
                                                                      create_task
                                                                      create_session_log
                                                                      create_node
```

Two responsibilities, split exactly the way `set_content` already splits them, and for the same reason:

* a **pure argument guard** (are the two body inputs mutually consistent?) — no I/O, callable from each
  tool's `_check_invariants`;
* an **I/O resolution** (turn whichever input was given into bytes, or refuse) — welded to the `open`.

---

## 5. Components & Responsibilities

### 5.1 `tools/_content.py` — shared body resolution (new)

Sits alongside the existing shared helpers `_groups.py`, `_substance.py`, `_link_details.py`, which is
the established home for logic more than one composite tool needs.

**Owns:** the mutual-exclusion rule between an inline body and a file body; the containment-gated read
of a file body; the three file-read outcomes and the zero-byte refusal; the exact wording of those
messages, so five tools cannot drift apart.

**Does not own:** whether a body is *required* (that is per-tool and stays per-tool — see §8); the
content type; anything to do with HTTP; anything to do with node creation, groups, links or substance.

### 5.2 The four composite creators — unchanged responsibilities, one new input

Each keeps everything it owns today (group resolution, node POST, link fan-out, substance, partial-state
reporting) and gains exactly one input. None of them learns what a filesystem is: they hand two optional
values to the helper and receive bytes or a refusal.

### 5.3 `divoid_set_content` — becomes a consumer of the logic it currently hosts

The behaviour it exposes must not change: same parameters, same error codes, same messages, same
byte-for-byte upload. Its own `content_empty` and `content_path_required` checks stay local, because
"a body is mandatory here" is a property of `set_content`, not of body resolution.

**Why this satisfies #6104 §3b without extracting `_execute` from three creators.** §3b's rule is that
the gate must not sit somewhere the opening function can be reached around; its named failure mode is
*"checking one string and opening another."* Welding the gate and the `open` into a single function
makes both failures unconstructible: there is no path-accepting code in this server that does not call
the gate, because the only `open` lives behind it. The rule is satisfied by co-location, which is
stronger than satisfying it by choice of enclosing function name. The helper is itself module-level and
directly drivable by the smoke seam, so the testability motive behind the `_execute` split is met too.

---

## 6. Interactions & Data Flow

### 6.1 The ordering invariant (the load-bearing sequencing decision)

> **Body resolution completes before the node POST is issued.**

```
  caller
    │  create_documentation(name=…, path=…, project_id=…, extra_links=[…])
    ▼
  ┌──────────────────────────────────────────────────────────┐
  │ 1. _check_invariants                                     │  pure; no I/O, no HTTP
  │      • link-target exclusivity   (existing)              │
  │      • body exclusivity          (shared guard, NEW)     │
  │      • body-required rule        (existing, see §8)      │
  ├──────────────────────────────────────────────────────────┤
  │ 2. resolve body  ──►  gate ─► open ─► bytes              │  disk I/O only
  │      any refusal returns here, and NOTHING has been      │  ◄── no node exists yet
  │      created                                             │
  ├──────────────────────────────────────────────────────────┤
  │ 3. GET  group resolution      (existing)                 │
  │ 4. POST /nodes                (existing)                 │  ◄── first mutation
  │ 5. POST /nodes/{id}/content   (existing, now with the    │
  │        resolved bytes)                                   │
  │ 6. POST /nodes/{id}/links ×N  (existing)                 │
  │ 7. PATCH substance            (existing)                 │
  └──────────────────────────────────────────────────────────┘
```

Steps 5–7 keep their existing `partial_state` semantics unchanged. The point of step 2's position is
that **the entire class of path failures — outside-root, sensitive, missing, unreadable, zero-byte —
is now a plain refusal rather than an orphan node.** If resolution ran after step 4, a mistyped path
would leave a content-empty node behind, which is the exact structural defect (#493 §4) this feature
exists to stop producing.

### 6.2 Failure surface

| Stage | Outcome on failure | Node created? |
|---|---|---|
| 1 — argument guard | `InvariantViolation` envelope | No |
| 2 — body resolution | error envelope carrying the helper's code | No |
| 3 — group resolution | `docs_group_not_found` / `tasks_group_not_found` (existing) | No |
| 4 — node POST | mapped HTTP error (existing) | No |
| 5–7 | `partial_state` naming the surviving id (existing) | Yes |

---

## 7. Contracts & Interfaces (abstract)

### 7.1 Shared guard — "are these two body inputs consistent?"

| Aspect | Contract |
|---|---|
| Inputs | The optional inline body; the optional file path |
| Effect | Raises when both are supplied; silent otherwise |
| Code raised | `content_path_conflict` (reused verbatim from `set_content`) |
| Purity | No I/O of any kind. Safe to call from `_check_invariants` |
| Non-responsibility | Does **not** decide whether a body is required. Both-absent is not its business |

### 7.2 Shared resolution — "turn whichever was given into bytes"

| Aspect | Contract |
|---|---|
| Inputs | The optional inline body; the optional file path; a short label naming the calling tool, for the log line |
| Output | Either a byte string, or a refusal envelope in the server's standard error shape — never both, never neither |
| Both absent | Yields "no body", not a refusal. The caller decides whether that is legal |
| Inline body given | Encoded to UTF-8 bytes. Behaviourally identical to what all five tools do today |
| Path given | Gated, then opened in binary, then returned unchanged — no decode/re-encode step at any point |
| Refusal codes | `path_empty`, `path_outside_root`, `file_root_unusable`, `path_denied_sensitive`, `file_not_found`, `file_read_failed`, `file_empty` — all pre-existing, none invented |
| Guarantee | The only path opened is the one the gate returned |
| Guarantee | No network call is made on any refusal |
| Ordering obligation on callers | Must be invoked before the first mutating HTTP call |

### 7.3 The creators' new parameter

| Aspect | Contract |
|---|---|
| Name | `path` |
| Type | Optional string; absent by default; every existing call is unaffected |
| Meaning | A local file whose bytes become the node's body, verbatim |
| Exclusivity | Mutually exclusive with `content` |
| Content type | Unchanged: `text/markdown; charset=utf-8`, as today, not inferred from the file extension |
| Description obligation | The model-facing description must name `path_denied_sensitive`, state that it is deliberate, that no alternative path works, and that copying the file to another name is **not** the remedy (#6104 checklist step 6) |

### 7.4 Why `path` and not `content_path`

`path` is what both existing filesystem-path tools call it (`download_content` `:168`, `set_content`
`:201-205`). Introducing `content_path` would create a **third** convention for one concept — the same
argument #8528 used to refuse extension-inferred content types. The one cost is that `divoid_list` uses
`path` for a graph-traversal expression; that collision already exists between `divoid_list` and
`set_content` and is not made worse by four more filesystem uses. The description text disambiguates.

### 7.5 Refusal messages must distinguish *absence* from *refusal*

A containment refusal and a missing feature read identically to a caller who never knew the parameter
existed. `path_outside_root` and `path_denied_sensitive` both say, in effect, *"this path will not be
used"* — and an agent that has not seen `path` in the schema concludes the tool cannot take a file at
all, then falls back to raw REST. That is the failure the whole MCP-first policy exists to prevent, and
it is a **description defect, not a gate defect**: the gate is behaving exactly as designed.

This is not hypothetical. It is the leading explanation for #11388 (see R5), which reported that
`set_content` has no file-upload path seventeen days after `31ce716` shipped one.

**Obligation on the tool description and the `path` parameter docstring of each of the four creators** — bounded 2026-09-07; see the note below the table:

| Must convey | Because |
|---|---|
| That the tool **does** accept a file body | Otherwise the caller concludes the feature is absent and stops looking |
| That *this particular path* was refused, and on which of the two grounds | `path_outside_root` is fixable by the caller (choose an in-root path); `path_denied_sensitive` is not, and the difference decides what the caller should do next |
| That the refusal is a boundary, not a fault | Existing wording, retained verbatim — it is what stops the `curl` fallback |

The distinction is one clause, and it is the difference between a caller who re-spells the path and a
caller who files a duplicate gap report. Milestone 4 makes it an acceptance item.

### §7.5 note — the original scope sentence, withdrawn

> **WITHDRAWN 2026-09-07.** *"Obligation on every refusal these tools emit."*

**What falsified it** (QA #13223 W-3): the remedy landed — well, and verbatim — in the **static** surface:
the four tool descriptions and their `path` parameter docstrings. The **runtime** refusal envelopes are
still built in `paths.py`, which this change does not touch, so no runtime message says the tool accepts
a file. "Every refusal these tools emit" claimed both halves.

**The scope that is correct, and sufficient.** The reader this section targets is the one deciding
whether the parameter exists at all, and that reader is reading the description — which now says so
explicitly. A caller who *receives* `path_outside_root` at runtime necessarily passed `path=` and
therefore already knows the parameter exists; for them the runtime message only has to separate the
fixable ground from the permanent one, which the existing `paths.py` wording already does. The static
half is where the gap was and where the fix belongs.

**Not deferred debt, and no follow-up is filed.** Extending the wording into `paths.py` would change
messages shared with `download_content` to solve a problem that half does not have, for a reader who
cannot be confused in the way this section describes. That is scope this section does not earn.

---

## 8. Composition with the content-required invariant

This is the question the brief flagged as sharpest, and it has two independent halves.

### 8.1 "Is a body required?" — per tool, unchanged codes

| Tool | Rule today | Rule with `path` | Code on violation |
|---|---|---|---|
| `create_documentation` | body required, non-whitespace | **exactly one** of `content` / `path` required | `content_whitespace_only` (unchanged) |
| `create_session_log` | body required, non-whitespace | **exactly one** required | `content_whitespace_only` (unchanged) |
| `create_task` | required unless `status="new"` | required unless `status="new"`, satisfiable by either | `content_required` (unchanged) |
| `create_node` | optional | optional; at most one | — |
| `set_content` | exactly one required (already) | unchanged | `content_path_required` (unchanged) |

Each tool keeps its own code and its own message. **No existing error code changes meaning, spelling or
wording.** A caller that passes neither gets exactly the error it gets today.

### 8.2 "Is the supplied body empty?" — asymmetric, deliberately, and identically to `set_content`

| Input | Verdict | Rationale |
|---|---|---|
| Inline body that is empty or whitespace-only | Refused, existing per-tool code | Existing behaviour, unchanged |
| File that reads as **zero bytes** | Refused, `file_empty` | The #7878 incident (a zero-byte upload wiped node #7872) is the recorded harm. Here the harm is a permanently inert node instead of a wipe — the same guard catches both |
| File whose bytes are **whitespace only** | **Uploaded as-is** | Matches `set_content` exactly |

The last row is the one worth defending, because it is an asymmetry: an inline `"   "` is refused, a
file containing `"   "` is not.

**Rejected alternative:** tighten the rule for the content-required creators so a whitespace-only file
is refused too. It is tempting, because `set_content`'s stated reason for the looser rule — *"`path` may
point at a binary file for which stripping would be wrong"* — does not apply to creators that post
markdown by construction.

**Rejected because** it would make the emptiness rule depend on *which tool* you called, for a case with
no recorded incident, while the case that does have a recorded incident is already caught. That is a
second rule bought with no evidence, and #1136 §6 names *"defensive code for impossible scenarios"* as
fix-on-sight. `set_content`'s own W-1 (#8528) considered this exact question, chose zero-byte-only, and
was upheld in QA (#8533). One rule across five tools beats a rule with an exception.

**Obligation that comes with the choice:** the asymmetry must be *stated* in each creator's description,
because a reader who sees `content` reject whitespace will otherwise assume `path` does too. #8528's W-1
fix is the model: it did not change the guard, it changed the sentence.

---

## 9. Cross-Cutting Concerns

| Concern | Position |
|---|---|
| **Security / containment** | Unchanged and inherited. Four more tools reach the same gate, with the same roots and the same sensitive-name refusal. There is no opt-out and this design does not add one |
| **Exfiltration direction** | These are *upload* tools: a refused path protects a secret from being *published into the graph*. `path_denied_sensitive` is the primitive that stops `create_documentation(path=…/.env)` from becoming a readable node |
| **Credentials** | Untouched. Invariant 1 holds: nothing in this design moves the key |
| **UTF-8 (#187)** | Strengthened. The file branch never decodes, so it cannot re-encode wrongly |
| **Observability** | One log line per resolution naming the tool, the source (`content` or `path`) and the byte length — the shape `set_content` already emits |
| **Error handling** | Every refusal reuses an existing code. The model-facing text must keep saying that containment refusals are boundaries, not faults, so an agent does not "route around" to raw REST |
| **Idempotency / retries** | Unchanged. No retry is introduced. Resolution is a pure read and is safe to repeat, but nothing repeats it |
| **Concurrency** | None introduced. The file is read once, synchronously, before any network call |
| **Consistency** | Improved: the window in which a placeholder body is visible to peers disappears for the single-call case |

---

## 10. Quality Attributes & Trade-offs

### 10.1 The DRY arithmetic, per #1136 §1 and #1267

Measured at `f8991d2`:

| Block | Size | Sites if inlined | Product | Threshold | Verdict |
|---|---|---|---|---|---|
| Gate + open + three file outcomes (`set_content.py:124-163`) | 40 lines | 5 | **200** | ~15–20 | Extract |
| Mutual-exclusion guard (`set_content.py:81-92`) | 12 lines | 5 | **60** | ~15–20 | Extract |
| Combined | 52 lines | 5 | **260** | ~15–20 | Extract |

Both are an order of magnitude over the threshold, so the extraction is not a judgement call. The
named-helper test also passes: *resolve body* and *guard exclusive* are each nameable in two words.

Secondary argument, and the one that actually matters: these blocks carry **message text an agent reads
and acts on**. Five hand-maintained copies of *"this is an intentional containment boundary, not a tool
defect — do not fall back to raw REST"* is five chances for one copy to soften and one agent to `curl`.

### 10.2 Net size

| Change | Lines |
|---|---|
| New `tools/_content.py` | ≈ +70 |
| Removed from `set_content.py` | ≈ −40 |
| Added per creator (parameter, docstring, two calls, description text) × 4 | ≈ +80 |
| **Net production** | **≈ +110** |
| New tests (helper ≈ 8, creators ≈ 4 each) | ≈ 24 |

### 10.3 Trade-offs made explicit

| Trade-off | Call | Reasoning |
|---|---|---|
| Four creators vs. only `create_documentation` | **Four** | See §10.4 — this is the one call a reviewer could reasonably trim |
| One emptiness rule vs. per-tool tightening | **One** | §8.2 |
| Extract shared helper vs. duplicate into four creators | **Extract** | §10.1; 260 ≫ 20 |
| Refactor `set_content` onto the helper vs. leave it and duplicate | **Refactor** | Leaving it makes the helper the second copy, which is the defect this fixes. Cost is a regression risk, bounded by 17 existing tests that must pass unchanged |
| Add `content_type` to the creators | **No** | No filing, no consumer, YAGNI (#1136 §3). Open question O4 |
| Extract `_execute` from three creators | **No** | §5.3 — the motive is met by welding; a large unrelated refactor is not this PR |

### 10.4 The scope call I am least certain about, stated as such

The invariant-hole argument (§1) covers three creators: `documentation`, `session_log`, and `task` at
any status other than `new`. `create_node` has no content-required invariant, so its case rests only on
the fidelity argument — which is real (a long `bug` or `plan` body drifts exactly like a design doc
does) but weaker, because nothing structural breaks without it.

I recommend all four anyway, for a reason that is about agent behaviour rather than code: **an
asymmetric surface produces REST fallbacks.** An agent that has used `path` on `create_documentation`
will try it on `create_task`, get a schema rejection, and do the thing the policy exists to prevent.
The marginal cost of the fourth creator, once the helper exists, is ≈ 20 production lines and ≈ 4 tests.

**If a reviewer disagrees, the trim is clean and I have made it so:** drop the parameter from
`create_node` and `create_task`, keep the helper, keep the other two. Nothing else in the design moves.

---

## 11. Alternatives Considered and Rejected

| # | Alternative | Rejected because |
|---|---|---|
| 1 | **Do nothing; document the two-step better** (the null result the brief invited) | The two-step's only form is *create a documentation node with a placeholder body*, which `create_documentation`'s own invariant message forbids in terms — *"do not create the node until you have the document."* Documentation cannot fix a workflow whose only shape violates the invariant the tool enforces. It also leaves a lying node visible to peers, and a permanent one if step 2 fails. This is the alternative I looked hardest at, and it fails on correctness, not ergonomics |
| 2 | **A dedicated `divoid_upload_content` tool** (#9876's option B) | #9876 argued against its own option B: a separate upload tool does not preserve the atomic Docs-group resolution and `extra_links` that made the composite create worth calling. It is also a *new tool*, which unambiguously requires sign-off (§12), and #8528 already set the precedent against it |
| 3 | **Creators delegate the whole content step to `set_content`'s executor** | Two structural mismatches: its result is a success/error envelope the creators would have to unwrap and re-wrap as `partial_state`, and it *posts* — so resolution would happen after node creation, breaking the §6.1 ordering invariant and reintroducing the orphan. A bytes-returning resolver is the smaller and correctly-ordered seam |
| 4 | **Name it `content_path`** | Third convention for one concept; §7.4 |
| 5 | **Refuse whitespace-only file bytes on content-required creators** | §8.2 |
| 6 | **Add `content_type` to the creators** | §10.3; YAGNI |
| 7 | **Infer content type from the file extension** | Already rejected for `set_content` in #8528 (mapping table to maintain, guessy for ambiguous extensions). Nothing has changed |
| 8 | **A `strict`/`allow_empty` knob on the emptiness rule** | #1136 §3: no named operator, no environment difference, no tuning event. A knob here is a magic number with indirection |

---

## 12. The sign-off question (brief §5) — my reading, and why

`divoid-mcp/CLAUDE.md` §"Tool surface" says:

> *"New tools require human sign-off from the repo owner before implementation — this is a
> generic-purpose tool used outside this deployment, so the surface evolves deliberately."*

**Literal reading:** the sentence's subject is *new tools*. A parameter is not a tool. #6104's
how-to-extend checklist restates the rule as step 1 of *"How to add a new tool."* On this reading the
present change does not need sign-off.

**Purposive reading:** the stated *reason* is that the surface evolves deliberately because external
consumers depend on it — and a parameter is surface. External consumers see the JSON Schema change. On
this reading the rule reaches parameter additions.

**The measured precedent, which decides it.** PR #172 / commit `31ce716` (2026-08-19) added the `path`
parameter to `divoid_set_content` — the *identical shape* to this change. Its design record (#8528)
addresses this sentence head-on:

> *"Deliberately a parameter on the existing tool, not a new `divoid_upload_content` tool, per
> `divoid-mcp/CLAUDE.md` §Tool surface (new tools need human sign-off; nobody had given it, and the
> parameter form is also simpler — KISS)."*

So the rule has been adjudicated once before, in this exact shape, and read literally — with the
sign-off requirement cited as a reason to *prefer* the parameter form. That PR went through QA (#8533),
which rejected it on three CFs; none of them was "this needed sign-off." The convention is therefore
not merely arguable, it is established by application and survived review.

### My ruling

**The literal reading applies. This change does not require sign-off** — with one qualification that I
think is the real content of the rule:

> The literal reading is safe **because the change is purely additive and optional**. No existing call
> changes behaviour, no default moves, no error code changes meaning, no accepted input is narrowed.
> A parameter change that altered a default, narrowed accepted input, or repurposed a name **would**
> engage the purposive reading and should be treated as requiring sign-off — because an external
> consumer's working call would break, which is precisely the harm the sentence names.

This design is on the safe side of that line by construction, and the constraint is written into the
acceptance criteria (§16, milestone 2: `set_content`'s 17 existing tests pass **unchanged**).

**And this document is the gate.** I am not asking for sign-off; I am noting that Toni reads the TL;DR
and can stop it there. I have not softened the design in anticipation of that.

**Recommendation (optional, one line of scope):** the sentence is ambiguous enough that two designs have
now had to adjudicate it. It would be cheap to make it say what it means — that new tools *and*
behaviour-changing modifications to existing ones require sign-off, while purely additive optional
parameters do not. That is a `CLAUDE.md` edit, listed as milestone 6, and it is genuinely optional.

---

## 13. Risks & Mitigations

| # | Risk | Severity | Mitigation |
|---|---|---|---|
| R1 | The `set_content` refactor silently changes an error message or code | Medium | Acceptance criterion: all 17 existing `test_set_content.py` tests pass **unchanged** — not adapted. If a test needs editing, the refactor changed behaviour and is wrong |
| R2 | A creator opens a raw path itself, bypassing the gate | High if it happened | Structurally prevented: the gate and the `open` are one function, and no creator gains any filesystem code. **Verification: no creator module contains `open(` or `paths.gate` at all** — that is the check that actually tests this risk. Behavioural pin: mutating the read to open the caller's raw string instead of the gate's resolved path must redden the resolved-path spy tests. The original, unbounded wording is retracted in place below |
| R3 | Resolution drifts to after the node POST during implementation, reintroducing the orphan | Medium | A test that supplies a bad path to each creator and asserts **no** `POST /nodes` was issued (respx makes this directly observable), not merely that an error came back |
| R4 | The mandatory sensitive-path sentence is omitted from a creator's description, so agents keep proposing denied paths and reading refusals as friction | Medium | #6104 checklist step 6 is an explicit acceptance item in milestone 4; the sentence is copied, not paraphrased |
| R5 | The feature is inert until the pinned operational install is refreshed and the host restarted | Medium | Documented in §14. **The stale-install branch was measured and is false today:** the deployed `divoid_mcp` carries `path` and `path_denied_sensitive` in `set_content`, and — separately confirming this design's premise — **zero** `path` parameters across the four creators (§18). So #11388's report is not explained by a missing feature. The surviving explanation is a **containment refusal read as absence**, which is a description defect, and §7.5 is its remedy |
| R6 | An agent passes both `content` and `path` | Low | `content_path_conflict`, reused verbatim, raised before any I/O or HTTP |
| R7 | A very large file is read fully into memory | Low | Unchanged from `set_content`; #8528 corrected its own record to say no failing size was ever *probed*. Not made worse here, and still no evidence justifying streaming |

### R2 note — the original verification sentence, withdrawn

> **WITHDRAWN 2026-09-07.** *"Verification: after the change, `open(` and `paths.gate` occur in exactly
> one module under `tools/`."*

**What falsified it** (QA #13223 CF-3, measured on the implementation):

```
download_content.py:75   resolved_path = paths.gate(path)
download_content.py:139  with open(resolved_path, "wb") as fh:
_content.py:82           resolved_path = paths.gate(path)
_content.py:88           with open(resolved_path, "rb") as fh:
```

**Two** modules, not one. `download_content.py` is the pre-existing **write** side — content coming
*out* of the graph onto disk. It has always had its own correct gate/open pair, and this change neither
touched it nor could have.

**Why the sentence was wrong.** It was written about the read/upload half, which is the half this design
governs, and then stated as a property of `tools/` as a whole. That is the same defect this document
warns about elsewhere: **a sweep proves nothing about the paths it was not given.** The claim was true of
the half it was written about and false as written.

**Why it matters enough to retract rather than quietly fix.** R2 is a High risk and this was its *only*
recorded mitigation, shipping in the same diff as the code. A maintainer running that grep after merge
gets a **false red** and concludes the containment weld broke. A verification instrument that reports a
failure where none exists is worse than no instrument — it spends exactly the attention it was built to
direct.

**The safety property itself was never in question and is independently pinned.** No creator gained any
filesystem code, and QA's M8 mutation (open the caller's raw string instead of the gate's resolved path)
reddened precisely the two resolved-path spy tests. Constraint C4 / #6104 §3b holds. The correction is to
the sentence, not to the code.

---

## 14. Migration / Rollout

There is nothing to migrate. Every new parameter is optional and defaults to absent; every existing call
is byte-for-byte unaffected; no stored data is touched; the server is a stateless client wrapper.

Two operational preconditions apply, both pre-existing and both already satisfied for `set_content`:

1. **`DIVOID_MCP_FILE_ROOT` must name a usable root**, or every `path` call returns
   `file_root_unusable`. Per #7739, a 100 KB design was filed via `set_content(path=…)` on this machine
   on 2026-09-07 and verified byte-identical, so the roots are configured. The creators inherit exactly
   that configuration — no new variable, no new default.
2. **The operational MCP is a pinned, non-editable git install.** The feature does not exist for any
   session until the install is refreshed and the host fully restarted (divoid-mcp CLAUDE.md
   §"Install / update the operational MCP"). See R5.

The deployed artifact currently demonstrates the gap between those two events. Measured (§18): the
installed package reports `__version__ = 0.11.0` while its own pip metadata says `Version: 0.10.0` —
the skew PR #187 closed at source earlier today, still live in the running server because it has not
been reinstalled. **A merge is not a deployment** (#6108 §4), and this feature will sit in exactly the
same state between its merge and its reinstall. Anyone verifying it against the running MCP rather
than the source tree should check the install first, or they will measure the old artifact and file
what #11388 filed.

---

## 15. Decisions taken

All five questions this document opened with were answered on 2026-09-07. None changed the design.

| # | Question | Decision |
|---|---|---|
| **O1** | Does the sign-off rule (§12) reach this change? | **The literal reading applies — no sign-off required**, accepted *with* the qualification that carries it: the reading is safe only because the change is purely additive and optional. A parameter change moving a default or narrowing accepted input would engage the purposive reading. Flagged to Toni prominently rather than buried, since it is his rule; he can stop the change at the TL;DR. Nothing here was softened on that account. Follow-up filed as **#13217** |
| **O2** | All four creators, or trim? | **Four.** The reason is the one in §10.4: an asymmetric surface is what produces REST fallbacks. §10.4's clean trim stays in this document as the recorded alternative, not as a pending question |
| **O3** | Why does #11388 postdate the fix by 17 days? | **Measured, and the worse branch is false.** The deployed install carries `path` on `set_content`; no session is running a pre-`31ce716` build. The surviving explanation is a containment refusal read as absence — a description defect, now designed against in **§7.5** and carried as an acceptance item in milestone 4. Triage of #11388 and #8440 belongs to the coordinator and is not chased here |
| **O4** | Is `content_type` on the creators YAGNI? | **Yes.** Only constructible case is `create_node(path=…)` with a non-markdown body, and nobody has filed it |
| **O5** | Does the CLAUDE.md sentence get sharpened in this PR? | **Not in this PR, and not dropped.** Governance in a feature PR makes one review answer two questions. **Filed as #13217**, linked to this design and to #8528, recording that the adjudication has now happened twice with the same outcome |

The `_ALLOWED_STATUSES` finding (§2 row 6) was likewise filed rather than argued into this PR:
**#13219**, citing the PR #157 precedent so the next reader inherits the scope boundary instead of
re-litigating it.

---

## 16. Implementation Guidance for the Next Agent

No code appears below by design. Each milestone is an independently reviewable unit; they are ordered by
dependency. **This is one PR** — the milestones are review checkpoints, not separate PRs, because
milestone 2 is a pure refactor whose only justification is milestone 3.

**Read first:** #114 (Code Contracts — governs this repo's Python, per QA #13153 CF-7), #1136 (Design
Contracts), `divoid-mcp/CLAUDE.md` (the six invariants), #6104 (tool anatomy — especially §3b and
checklist step 6), #8528 (the precedent this design extends).

| # | Milestone | Done when |
|---|---|---|
| **1** | **Create the shared helper module** under `tools/`, alongside `_groups.py` / `_substance.py`. Two responsibilities only, per §7.1 and §7.2. Every code and every message string is **moved, not rewritten** — the wording is load-bearing agent-facing text (§10.1) | The module holds the only `paths.gate` call and the only `open` call under `tools/`, and its messages are textually identical to `set_content`'s at `f8991d2` |
| **2** | **Re-point `set_content` at the helper.** Its local `content_empty` and `content_path_required` checks stay | **All 17 existing `test_set_content.py` tests pass unchanged.** A test that needs editing is the signal that behaviour moved — stop and reconsider rather than adapting the test |
| **3** | **Add `path` to `create_documentation`** — the filed shape. Guard call in `_check_invariants`; resolution as the first act of the create flow, **before** group resolution and the node POST (§6.1) | A bad path yields a refusal and **zero** HTTP calls; a good path yields a one-call node whose body is byte-identical to the file |
| **4** | **Extend the description text** for `create_documentation`: what `path` is, the zero-byte-vs-whitespace asymmetry (§8.2), the mandatory `path_denied_sensitive` sentence per #6104 step 6 — copied, not paraphrased — and the §7.5 absence-vs-refusal distinction | Five clauses present: names the code, says it is deliberate, says no alternative path works, says copying to another name is not the remedy, **and makes clear the tool does accept a file — it is this path that was refused** |
| **5** | **Repeat 3–4 for `create_session_log`, `create_task`, `create_node`**, respecting each one's own required-rule from §8.1. `create_task`'s `status="new"` case must still permit neither input | Each creator's existing content-required code and message are unchanged in spelling and meaning |
| **6** | ~~Sharpen the `CLAUDE.md` sign-off sentence~~ — **not in this PR.** Filed as **#13217**; governance does not ride along in a feature PR (O5) | Out of scope. Listed so a reader does not re-open it |

### Test obligations (these are the load-bearing ones — #114, and the load-bearing-test discipline)

1. **Byte-identity, not a success envelope.** For at least one creator, assert the *outbound request
   body* equals the file's bytes, using a file that carries the characters that broke the reported
   incidents: an unescaped `|` inside a markdown table, CRLF line endings, a multi-byte UTF-8 character,
   a box-drawing character, an em-dash, and a lone trailing `\r`. #8528's CF-2 is the precedent for why
   asserting a value the tool built locally proves nothing — assert on the wire.
2. **No-orphan.** Per creator: a refused path issues **no** `POST /nodes`. Assert on the absence of the
   request, not on the shape of the error.
3. **Gate reachability.** Per creator: an out-of-root path and an in-root sensitive path are both
   refused, and the file is never opened.
4. **Exclusivity and required-rules.** Both-given per creator; neither-given per creator against that
   creator's own existing code; `create_task` with `status="new"` and neither given must still succeed.
5. **Mutation-test every new guard branch** — mutate the production line, confirm exactly its own test
   reds and nothing else, revert. This is the standard #8528 was held to and it caught a vacuous test
   there.
6. **Smoke coverage** in `tests/smoke/run_all.py` importing the helper by name (#6104 checklist step 4 —
   a smoke test that does not import the real function proves nothing). Note #8528's CF-1: a signature
   change broke existing smoke call sites silently. Grep the smoke suite for every call site of anything
   this change touches.

---

## 17. Pre-Design Checklist (#1136 §5)

**KISS / DRY / YAGNI**
- [x] No new type mirroring an existing one — no types are introduced.
- [x] No new abstraction with one implementation — the helper has five callers on day one.
- [x] No element justified by "we might need X later." `content_type`, streaming and a strictness knob were each considered and dropped (§10.3, §11).
- [x] No deprecation period, feature flag, compatibility shim, or transition window — the change is additive.
- [x] DRY math quoted: 40 × 5 = 200 and 12 × 5 = 60, combined **260** against a ~15–20 threshold (§10.1). Extraction, not inlining.

**Existing systems first**
- [x] Audited: the gate (`paths.py`), the read, the refusals and the error vocabulary all exist. This design adds **no new mechanism** — it relocates one and calls it four more times.
- [x] No new layer proposed. The new module is a shared helper in the directory that already holds three of them.
- [x] No new persisted data.
- [x] No field justified by "an existing reader projects it."

**Configurability**
- [x] No new config knob. `DIVOID_MCP_FILE_ROOT` is inherited unchanged; no new variable, no new default.
- [x] No telemetry-then-tune compound.
- [x] No magic numbers introduced.

**Less is better**
- [x] Can-it-be-deleted run on every element: the helper cannot (260-line duplication otherwise); the fourth creator's parameter can, and §10.4 says so explicitly and names the trim.
- [x] Trade-offs named where the simple option lost: §8.2 (one emptiness rule), §10.3, §10.4.
- [x] Radical-clean chosen over compromise: `set_content` is refactored onto the helper rather than left as a second copy.
- [x] Inventories explicit: all five call sites enumerated; all seven inherited refusal codes enumerated (§7.2).

**Document discipline**
- [x] #114 and #1136 cited as load-bearing.
- [x] Out-of-scope items listed explicitly, with reasons (§2).
- [x] No multi-paragraph rationale for things that obviously stay.
- [x] Supersedes nothing — this extends #8528 rather than replacing it, and says so.

---

## 18. Measurement log

Every claim of current-state fact in this document comes from one of the following, run at `f8991d2`.
Commands that returned nothing are reported as such — a zero reported is evidence, a zero unreported is
an assumption (#11228 Lesson 1).

**On the form of these transcripts.** Every command below was run and its result reported honestly, but
the blocks dated to `f8991d2` and to the deployed install are **reformatted for readability** — path
prefixes stripped, columns aligned, one `git log` line elided. Re-running them against a later tree will
legitimately differ, and a difference is evidence that the tree moved, not that this document is wrong.
*Reformatted* is a different claim from *unrun*: the rule this document holds itself to is that a
prescribed command must have been executed and its result quoted, which these satisfy — the caption is
what makes them honest about the remaining gap between *run* and *verbatim*. **The one exception is the
wider-sweep block under "R2's corrected verification instrument", which is byte-literal**: it was
verified by extracting it from this file and `diff`-ing it against a live run, because a block whose
entire purpose is to correct an unrun claim has to be the thing it depicts. The general form is worth
stating once rather than defending repeatedly: **any dated transcript in a design ages out of literal
truth the moment the tree moves.**

**Commit under measurement**

```
$ git rev-parse HEAD
f8991d2b0ab29bc51612c76eb6c44355fec98ea2
```

**The gap: zero `path` parameters across the four creators**

```
$ grep -n "path" divoid-mcp/src/divoid_mcp/tools/create_documentation.py \
                 divoid-mcp/src/divoid_mcp/tools/create_task.py \
                 divoid-mcp/src/divoid_mcp/tools/create_session_log.py \
                 divoid-mcp/src/divoid_mcp/tools/create_node.py
create_documentation.py:6:  1. GET /nodes?path=[id:<project_id>]/[name:Docs]  (only if project_id is given)
create_task.py:6:           1. GET /nodes?path=[id:<project_id>]/[name:Tasks] (only if project_id is given)
create_session_log.py:6:    1. GET /nodes?path=[id:<project_id>]/[name:Docs]  (only if project_id is given)
create_node.py:6:           status values) and are the preferred path for their covered types. This tool is
```

Four hits, all four in prose or in the graph-query grammar. **Zero filesystem-path parameters.** The
#7739 triage measurement at `df3436b` re-verified here at `f8991d2`.

**The machinery exists, in exactly one place**

```
$ grep -rn "import paths\|paths\.gate" divoid-mcp/src/divoid_mcp/tools/*.py
download_content.py:25:from .. import http_client, paths
download_content.py:75:        resolved_path = paths.gate(path)
set_content.py:38:from .. import http_client, paths
set_content.py:126:            resolved_path = paths.gate(path)
```

Two callers of the gate. Neither is a creator.

**R2's corrected verification instrument, run against the implementation**

The sentence R2 originally carried was never run before it shipped, which is how it shipped false.
The replacement was run before this revision was written:

```
$ grep -n "open(\|paths\.gate" divoid-mcp/src/divoid_mcp/tools/create_documentation.py \
                                divoid-mcp/src/divoid_mcp/tools/create_task.py \
                                divoid-mcp/src/divoid_mcp/tools/create_session_log.py \
                                divoid-mcp/src/divoid_mcp/tools/create_node.py
(no output; grep exit 1)
```

Zero. No creator module contains filesystem code of any kind — which is the property R2 is actually
about. The wider sweep the withdrawn sentence prescribed reproduces QA's finding exactly, and shows
why it was the wrong instrument:

```
$ grep -rn "paths\.gate\|with open(" divoid-mcp/src/divoid_mcp/tools/*.py
divoid-mcp/src/divoid_mcp/tools/_content.py:10:  - resolve_body: I/O resolution, welded to the containment gate (paths.gate)
divoid-mcp/src/divoid_mcp/tools/_content.py:82:        resolved_path = paths.gate(path)
divoid-mcp/src/divoid_mcp/tools/_content.py:88:        with open(resolved_path, "rb") as fh:
divoid-mcp/src/divoid_mcp/tools/download_content.py:75:        resolved_path = paths.gate(path)
divoid-mcp/src/divoid_mcp/tools/download_content.py:139:        with open(resolved_path, "wb") as fh:
```

Five hits, and the shape of them is the whole point. Two are gate/open **pairs** — this design's
read/upload pair in `_content.py`, and the pre-existing write pair in `download_content.py` that the
design never governed. The fifth, `_content.py:10`, is a **docstring** that merely mentions
`paths.gate`; a text search cannot tell a call from a sentence about a call.

Both pairs are correct and only the sentence was wrong. But note what the withdrawn instrument would
have reported to a maintainer: five hits across two modules, one of them prose, against a criterion
demanding exactly one module. Every part of that reading is misleading, and none of it indicates a
defect.

**`path` is the established name for a filesystem path**

```
$ grep -n "async def divoid_download_content" divoid-mcp/src/divoid_mcp/tools/download_content.py
168:    async def divoid_download_content(node_id: int, path: str) -> dict[str, Any]:
```

Together with `set_content.py:201-205` (`id`, `content`, `path`, `content_type`), both existing
path-bearing tools spell it `path`.

**The precedent for the sign-off reading, dated**

```
$ git log --format="%h %ad %s" --date=short -- divoid-mcp/src/divoid_mcp/tools/set_content.py
51351de 2026-09-02 feat(divoid-mcp): refuse sensitive in-root paths in the path gate (DiVoid #10481)
e17a358 2026-09-01 fix(divoid-mcp): contain filesystem paths in download_content/set_content (#10473)
9663a14 2026-08-19 fix(divoid-mcp): address Jenny's QA review of #172 (DiVoid #8533)
31ce716 2026-08-19 feat(divoid-mcp): add file-based upload path to divoid_set_content (#8523, #7895)
ed86f77 2026-05-21 feat(divoid-mcp): polish primitives — patch_node + set_status + set_content + ...
```

`31ce716` is the parameter addition that shipped without sign-off (§12). It also dates R5: #11388,
filed 2026-09-05, reports the gap `31ce716` closed on 2026-08-19.

**The deployed artifact — R5, O3, and an independent confirmation of the gap**

Read at `C:\Users\max_g\AppData\Roaming\Python\Python314\site-packages\divoid_mcp`:

```
$ grep -n "path: str | None\|path_denied_sensitive\|paths.gate" $INST/tools/set_content.py
63:separately rejected with path_denied_sensitive -- this is deliberate and has
72:def _check_invariants(content: str | None, path: str | None) -> None:
112:    path: str | None = None,
126:            resolved_path = paths.gate(path)
204:        path: str | None = None,

$ grep -n "path: str" $INST/tools/create_documentation.py $INST/tools/create_task.py \
                      $INST/tools/create_session_log.py $INST/tools/create_node.py
(no output; grep exit 1)
```

The running server **has** the parameter on `set_content` and has **zero** on the four creators. Two
consequences: R5's stale-install explanation for #11388 is false today, and the gap this design closes
is confirmed in the deployed artifact and not only in the source tree.

**Version skew in the same install — a merge is not a deployment**

```
$ python -c "import divoid_mcp; print(divoid_mcp.__version__)"
0.11.0

$ pip show divoid-mcp | grep -iE "^(Name|Version|Location)"
Name: divoid-mcp
Version: 0.10.0
Location: C:\Users\max_g\AppData\Roaming\Python\Python314\site-packages
```

The module and its own package metadata disagree — the skew PR #187 closed at source earlier today,
still live in the deployed artifact because it has not been reinstalled (#6108 §4). §14 carries the
consequence for this feature.

**Existing test surface that milestone 2 must not disturb**

```
$ grep -c "^async def test" divoid-mcp/tests/unit/test_set_content.py
17
```

**Line ranges behind the DRY arithmetic (§10.1)**

| Block | File and lines | Count |
|---|---|---|
| Gate + open + `file_not_found` / `file_read_failed` / `file_empty` | `set_content.py:124-163` | 40 |
| Mutual-exclusion guard | `set_content.py:81-92` | 12 |

**Not measured, and stated as such**

* ~~Whether #11388's filer ran a stale pinned install or hit a containment refusal.~~ **Resolved by
  the install measurement above**: the deployed `set_content` has `path`, so the stale-install branch
  is false and the containment-refusal branch is the surviving candidate. What remains genuinely
  unmeasured is *which* refusal that caller hit — the graph does not record it, and §7.5 is designed
  to make the question unnecessary rather than answerable.
* The file size at which a single in-memory read fails — never probed, here or in #8528, which corrected
  its own record on precisely this point.

---

## 19. References

| Node / ref | What it is |
|---|---|
| **#7739** | Source task (post-triage scope: the composite creators) |
| **#9876** | Consolidated duplicate; source of the fidelity framing quoted in §1 |
| **#8440**, **#11388** | Two further open filings of the same gap (O3) |
| **#8528** | `set_content path=` design record — the precedent this design extends (§12) |
| **#8533** | QA of PR #172; three CFs whose lessons are carried into §16 |
| **#6104** | Tool anatomy and the how-to-extend checklist — §3b and step 6 are binding here |
| **#10479**, **#10543** | The path-containment and in-root-sensitive-refusal designs this inherits |
| **#7878** | The zero-byte-upload incident behind `file_empty` |
| **#187** | The UTF-8 mangling trap the byte path avoids |
| **#493 §4** | Content-required structural convention |
| **#114**, **#1136** | Code Contracts and Design Contracts |
| **#11228** | Retraction / sweep discipline — governs §18 |
| **#5860** | Repo Map — DiVoid |
| **#13217** | Filed follow-up: sharpen the CLAUDE.md sign-off sentence (O5) |
| **#13219** | Filed follow-up: `create_task`'s `_ALLOWED_STATUSES` vs invariant 6 (§2 row 6) |
| **#157** / **#5837** | The precedent that removed the identical status allow-list from `set_status` |
| **#6108 §4** | A merge is not a deployment — the pinned-install contract (§14, §18) |
| **#10895 §5.1** | File the follow-up node first, so the extension is bounded by a named remainder |
