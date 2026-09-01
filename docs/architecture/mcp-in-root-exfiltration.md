# Architectural Document: In-Root Sensitive-Path Refusal for `divoid-mcp`

**Status:** design, ready for implementation
**Author:** Sarah (software architect), 2026-09-01
**Source task:** DiVoid **#10481** · **Predecessor design:** DiVoid **#10479** (§10.1 named this hole; **not superseded** — this design extends it)
**Predecessor state:** PR **#175** merged; containment is live on `main` at `92de025`
**Standards applied:** Design Contracts **#1136** (load-bearing) · Code Contracts **#114 §0** (load-bearing) · `divoid-mcp/CLAUDE.md` invariants 1–6 · Tool anatomy **#6104** · Falsifiability addendum **#1220 §5** · Measurement-channel discipline **#10499**

---

## TL;DR

**What.** Containment (#175) bounds *where* a caller-supplied path may point. It does not bound *what*. Everything inside the session's checkout — `.git/config` with a token in the remote URL, any `.env`, `.npmrc`, key material — is still readable into a shared multi-agent graph in one `divoid_set_content(path=…)` call, and still overwritable by one `divoid_download_content` call.

**How.** No new module, no new call site, no new configuration. The existing path gate in `divoid-mcp/src/divoid_mcp/paths.py` grows **one more rejection reason**, evaluated *after* resolution and *after* the containment verdict: if any component of the resolved path, taken **relative to the root that containment matched**, matches a short list of credential-bearing and execution-configuring name patterns, the gate raises `InvariantViolation` with a new stable code `path_denied_sensitive`. Because both tools already call the same gate, the rule applies to the read side and the write side **by construction** — refusing to apply it to one of them would cost extra code, not save it.

**The central trade, stated up front.** This mechanism is **precision-optimised and deliberately low-recall**. It refuses a named set of files that no agent has a reason to publish into a shared graph; it does **not** claim that secrets cannot leave the root. Over-blocking is the failure mode that destroys the mechanism — a false denial pushes the caller back to inlining through `content` (measured to corrupt bodies, #8523/#7895) or to a raw REST fallback, which is worse than the hole. So the list is kept short, and its misses are named rather than argued away (§10).

**Measured friction on the legitimate path: three arguable cases, spread across three patterns.** Swept over the working tree on 2026-09-01: of 51,539 files, the predicate refuses 4,781 — 4,772 of them under `.git`. Outside `.git` the refusals are `.claude/settings.local.json`, `.vscode/settings.json`, two vendored `cacert.pem` CA bundles, `divoid-mcp/examples/.mcp.json`, `frontend/.env.example`, `frontend/.env.local`, and two `node_modules/**/.claude/settings.local.json`. Of those nine, **three** are files anyone might plausibly file to DiVoid — `examples/.mcp.json`, `frontend/.env.example` and `.vscode/settings.json` — and each is readable by other means. §9.3 classifies all nine; §3.1(c) explains why three cases across three distinct patterns does not trip the removal rule, which is per-pattern.

**Answer to the brief's central question — can this be closed without taxing the path the tool exists to serve?** **For a named subset, yes; for the class, no.** §12.1 shows the trade with both halves priced.

---

## 1. Problem Statement

`divoid_set_content(id, path=…)` reads a local file and posts its bytes into a DiVoid node. DiVoid is a **shared multi-agent graph**: what lands in a node body is visible to every agent and session that reads that node. After #175, the file must lie inside the session's checkout — and an ordinary checkout contains live credentials.

The reachability argument is #10472's and is unchanged by containment:

> *"DiVoid is untrusted shared content. A node body / task description / message that an agent is told to 'download to the path below' or 'set this node's content from `<path>`' is attacker-influenced argument selection. … Node content should be treated as untrusted when it names a filesystem path."*

Toni's originating words, on the parent finding:

> *"file that as critical to the divoid project … we should close that gap as soon as possible, especially uploading arbitrary files is serious, but writing to arbitrary paths is also uncool"*

**There is no verbatim user quote for this follow-up.** #10481 was filed on my own recommendation (#10479 §17 Q3) so that PR #175 could not be read as closing the topic. The success criterion this design is written against is the coordinator's, quoted verbatim:

> *"sourcing node content from a sensitive file inside the root is no longer a silent one-call operation."*

Read that criterion precisely, because it is what the design can and does deliver. It asks for the removal of **silence** and of **one-callness**. It does not ask for — and §10 explains why nothing name-based can provide — a guarantee that a determined, shell-capable agent cannot move a secret into the graph.

### 1.1 What #10481 offered, and what I did with it

#10481 lists a deny-list and a confirmation requirement as *"starting points from #10479 §10.1, not a shortlist."* Applying the same discipline #10479 applied to the finding's remedy:

| Taken | Re-derived, and landing elsewhere |
|---|---|
| The hole, and that containment cannot answer it | The mechanism family (deny-list **chosen**, confirmation **rejected by name**, §12.2) |
| The named targets `.git/config`, `.env`, `.claude/` | The actual membership rule and list — `.venv/` is **dropped** and `.claude/` is **not** denied wholesale, both on measurement (§4, §9.3) |
| The constraint that the mechanism must not tax the legitimate upload path | How that constraint is *discharged* — by optimising for precision and accepting low recall, which inverts the usual deny-list critique (§12.1) |
| That the read side is the subject | Whether the write side comes along — **it does**, by construction (§6) |

---

## 2. Scope & Non-Scope

### In scope

- `divoid-mcp/src/divoid_mcp/paths.py` — one additional rejection reason inside the existing gate, plus the pattern table it consults.
- One new stable error code and its message.
- The two `_TOOL_DESCRIPTION` strings (`set_content.py`, `download_content.py`) — the model-facing contract (#6104 §1).
- Unit coverage in `divoid-mcp/tests/unit/test_paths.py` (which exists and covers the gate today) and the two tool test modules; smoke coverage per #6104 step 4.
- `divoid-mcp/README.md` — one paragraph next to the existing containment paragraph.
- Version bump.

### Explicitly out of scope

- **Containment itself.** #175 is merged and reviewed. Nothing in this design changes the root list, the syntactic prefilter, resolution, or the containment comparison. Three discrepancies between #10479 and the shipped code were reported to the coordinator separately; none of them is a defect in the containment *design*.
- **Any configuration knob for this list.** §8.3 states why there is none.
- **Content inspection.** Entropy scanning, token-prefix detection, and any other look-at-the-bytes mechanism is rejected in §12.4.
- **The harness layer.** Unchanged from #10479 §14. This design's rule lives server-side because the server is the only layer that sees the resolved path (#10472's own words), and #10479 §14 C-6 already binds the server fix to be complete on its own.
- **Overwrite-in-root generally.** #10479 §10.3 is **narrowed, not closed** — see §10.2.
- **DiVoid backend changes.** C1 (pure client wrapper) is unaffected; nothing here touches the API.

---

## 3. Assumptions & Constraints

All of #10479 §3's constraints C1–C8 carry over unchanged. The two that do real work here:

| # | Constraint | Consequence for this design |
|---|---|---|
| C5 | Invariant 6 — the system layer never enforces client **vocabulary** | The sharpest objection to this change. Addressed head-on in §3.1 |
| C6 | Real logic lives in `_execute`, the smoke-test seam | The check goes inside the existing gate, which both `_execute`s already call before any I/O. Placement is inherited, not re-litigated |

| # | Assumption | Confidence | How established |
|---|---|---|---|
| B1 | The adversary is a *steered or mistaken* agent, not the agent itself | Inherited from #10479 §10.2, restated because it bounds every claim below | Stated, not measured. §10.4 names what changes if it is false |
| B2 | `os.path.realpath` collapses trailing dots/spaces and 8.3 short names in a path component | **Measured** 2026-09-01 (§9.2) — and it **contradicts a REASONED claim in #10479 §9.3**. The design does not rely on it regardless (§8.2) | Probe, written to disk, inputs built from `chr(92)` |
| B3 | No configured root is an ancestor-of-a-root whose directory name appears in the pattern list | Holds for every pattern below, by construction (§8.2 note) | Reasoned; the falsifier is named |

### 3.1 Invariant 6 — the tension, named rather than assumed away

The brief asks for this explicitly, and it is closer to the line than containment was. Containment had an easy answer: a filesystem path is never sent to the backend, so the backend has no opinion to override. **That answer is necessary here but not sufficient**, because this rule is not purely structural — it encodes a *judgement about what a file means*, and judgements about meaning are exactly what invariant 6 reserves to the client's evolving convention.

Three things resolve it, and the third is the important one.

**(a) No backend value-space is narrowed.** Invariant 6's concrete prohibition is on hard-coding allow-lists of *values the backend would accept* — status strings, node types, link types — which is what forced REST fallbacks in #5837. This rule narrows no such field. The backend never sees the path, and the same bytes remain postable by any other route the caller has.

**(b) No other layer can hold the rule.** #10472 states it plainly: *"This is the layer that can actually see the resolved path."* The backend cannot express the rule; the harness (§14 of #10479) is explicitly a deterrent that does not exist for other MCP hosts and can be disabled by anything that can edit host configuration — which, note, this very change now refuses to overwrite.

**(c) The #5837 failure mode is real here, and it dictates the design rather than forbidding it.** #5837's lesson was not "never encode policy" — it was *a client-side list that blocks something the deployment legitimately wants forces a REST fallback every time.* On this surface a REST fallback would both defeat the guard **and** re-expose the caller to the corruption `path` exists to avoid. So the lesson binds as a **design constraint on the list's shape**, not as a prohibition on having one:

> **Every false denial is a bypass, not an inconvenience.** Therefore optimise for precision — refuse only files no agent has a reason to publish — and accept low recall as a stated limit.

That is the same conclusion §12.1 reaches from the friction side, arrived at independently from the invariant. Two arguments converging on one shape is the strongest reason to believe it.

**What would falsify (c):** a deployment in which one of the named patterns routinely covers a file that legitimately becomes node content. The sweep of 2026-09-01 (§9.3) found **three** arguable cases in this working tree, one attributable to each of three different patterns: `divoid-mcp/examples/.mcp.json` (`.mcp.json`), `frontend/.env.example` (`.env*`), and `.vscode/settings.json` (`settings.json`).

**The tripwire is per-pattern, and that is load-bearing rather than convenient.** The remedy is *removal of a specific pattern*, so the count that decides it has to be attributable to a specific pattern; a global total cannot tell you what to remove. Read that way the rule has **not** fired: no single pattern has produced more than one arguable denial. Read as a global total it would already be at the threshold, so the distinction decides the outcome and is stated rather than left to the reader.

Two things follow, and a future reader should weigh both:

- **The rule stands as written**: if any *one* pattern produces a second and third arguable denial in operation, remove **that pattern** — never add an override knob (§8.3).
- **Three distinct patterns each producing one arguable denial is itself a signal**, even though it trips nothing. It is the shape that precedes a tripwire firing, and it is the reason §10.3 now points reviewers at list membership rather than at deny-list incompleteness.

The counts above are a **dated measurement, not a tally anyone maintains.** Re-running the sweep is the way to refresh them; nothing in the design or the code reads them.

---

## 4. Does a mechanism belong here at all?

#10479 §4 asked whether `path` should exist; this design owes the analogous question. Three answers had to be ruled out before designing anything.

**"Do nothing; the graph is only shared among this user's own agents."** Rejected. The graph's audience is the smaller half of the problem. A credential in a node body is a **live capability** — it is not merely disclosed, it is *usable*, indefinitely, by anything that later reads the node, including a future agent that has no idea where the string came from. That is categorically different from the disclosure of source code, and it is why §8.1's membership bar is written around credentials rather than around confidentiality.

**"Do nothing; a steered agent will just launder the file through a copy."** Rejected, but it is the strongest objection and it is answered honestly, not dismissed. See §10.1: laundering is real and unclosable by any name-based rule. The gain is that it converts *invisible misuse of a tool the agent uses all day* into *a shell command whose only purpose is to relocate a credential file* — a step that reads as evasion in a transcript, is subject to the harness boundary guard, and that the refusal message explicitly instructs a compliant agent not to take.

**"Wait for a mechanism that actually closes the class."** Rejected. There is none that does not tax the legitimate path (§12.2–§12.5 rule out every candidate I could construct). Holding out for it leaves the named targets open indefinitely.

---

## 5. Architectural Overview

Nothing in the shape of the system changes. One box in the existing gate grows one more exit.

```
   caller path ──►  ┌──────────────── PATH GATE (paths.py) ──────────────────┐
                    │ 1. roots empty                → file_root_unusable     │
                    │ 2. \\?\ \\.\ //?/ //./ prefix → path_outside_root      │  ← unchanged
                    │ 3. embedded NUL byte          → path_outside_root      │     by this
                    │ 4. realpath()  (any raise     → path_outside_root)     │     design
                    │ 5. normcase + component compare vs each root;          │
                    │    ValueError → try next root; none matched            │
                    │                               → path_outside_root      │
                    ├────────────────────────────────────────────────────────┤
                    │ 6. NEW: components of the resolved path RELATIVE TO     │
                    │    the root matched in step 5, each normalised and      │
                    │    matched against the sensitive-name patterns          │
                    │                               → path_denied_sensitive   │
                    └──────────────────┬───────────────────┬──────────────────┘
                                       │ pass              │ fail
                                       ▼                   ▼
                              RESOLVED path         InvariantViolation
                                       │             → make_error_content
                    ┌──────────────────┴─────────────────┐
                    ▼                                    ▼
        download_content._execute            set_content._execute
        (write side — gate before the GET)   (read side — gate before the open)
```

Four properties of that placement are load-bearing, and each is measured or argued below:

1. **Step 6 runs on the *resolved* path.** An in-root junction or symlink pointing at `.git` therefore cannot launder a denied path into an allowed name. **Measured** (§9.2) — the raw string `<root>\fakedir\config` has no `.git` component; the resolved one does, and the gate refuses it.
2. **Step 6 runs *after* step 5, on components relative to the matched root.** Matching against the components of the *absolute* path would refuse every path in a session whose checkout sits under a directory bearing a listed name. **Measured** (§9.2): the documented worktree location `<repo>/.claude/worktrees/<purpose>` is exactly that shape. This is not hypothetical — it is where `isolation: "worktree"` puts worktrees.
3. **Step 6 is inside the gate, so both tools inherit it.** Read and write are covered because there is no code that could exempt one. §6 states why that is the right call and what it does and does not buy on the write side.
4. **Step 6 changes no ordering.** The gate already runs before the HTTP call and before any disk touch, at both call sites. A refusal at step 6 therefore performs no network I/O and opens no file, exactly as a step-5 refusal does today.

---

## 6. Components & Responsibilities

| Component | Change | Owns | Does **not** own |
|---|---|---|---|
| **`paths.py`** | the pattern table + step 6 inside `gate()` | deciding whether a *contained* path names a file the server refuses to read or write | any I/O; any knowledge of which tool called; any distinction between read and write |
| **`errors.py`** | none | `InvariantViolation` and the envelope, reused verbatim | — |
| **`set_content._execute`** | none | already calls the gate before `open` | — |
| **`download_content._execute`** | none | already calls the gate before the `GET` | — |
| Both `_TOOL_DESCRIPTION` strings | new sentence | teaching the model the rule before it proposes a denied path | — |

**Why the check is symmetric across read and write, stated as a decision rather than an accident.** The brief asks whether this design addresses the read side, the write side, or both, and why.

It addresses **both**, and the direction-symmetry is *the absence of an exception* rather than an added feature: the gate is one function with two callers, so suppressing the check for one of them would require a parameter, a branch, and a reason. There is no such reason, and each direction stands on its own justification:

- **Read side** (`set_content`) — the subject of #10481. Refusing the named files removes the one-call exfiltration of a live credential into shared storage.
- **Write side** (`download_content`) — refusing graph-controlled bytes onto `.git/hooks/*`, `.git/config`, `.mcp.json`, `settings.json` and `settings.local.json` removes the subset of #10479 §10.3's overwrite primitive that converts most directly into *later code execution or tool-surface expansion*. Writing a git hook or an MCP registration is a qualitatively different act from writing a source file.

**And the write side's limit, so this is not read as a completeness claim:** #10479 §10.3 stays open in its general form. Graph-controlled bytes can still be written over any source file, over `CLAUDE.md`, over `docs/**` — and over an *existing* hook script that a settings file already points at. The list narrows the write side to the self-configuration targets; it does not bound overwrite-in-root. That limit is restated in §10.2 where it belongs.

**Scope note for the packaging decision.** Folding the write side in is one predicate at one site — it costs nothing and skipping it would cost code. This is the same shape as #10479 §9.4's embedded-NUL guard: not unrelated work riding along, but the absence of a carve-out in the function the change already touches. **If the coordinator judges the write side out of #10481's scope, say so before John starts** — the cost of splitting is a parameter and a branch, i.e. strictly more code for less coverage, and I would want that recorded as a deliberate choice rather than implemented quietly.

---

## 7. Interactions & Data Flow

### 7.1 `divoid_set_content(path=…)` — refusal

1. The registered shim runs `_check_invariants(content, path)` — **unchanged**.
2. `_execute` calls the gate. Steps 1–5 pass (the path *is* contained). Step 6 matches.
3. The gate raises `InvariantViolation("path_denied_sensitive", …)`.
4. `_execute` returns the existing error envelope. **No file is opened. No HTTP request is issued.**

### 7.2 `divoid_download_content` — refusal

1. `_execute` validates `node_id` and non-empty `path` — unchanged.
2. Gate refuses at step 6. **No `GET` is issued, no directory is created, no file is opened.**

### 7.3 Success paths

Byte-identical to today for every path the list does not name. The gate returns the same resolved string it returns now, and both tools open the same file they open now.

---

## 8. Contracts & Interfaces (Abstract)

### 8.1 The membership rule — what makes a pattern eligible

The list is not a taste judgement, and it is not "files that feel private". A pattern is admissible only if **both** of the following hold:

1. **The file's contents are a live credential, or they configure what the agent host executes.** Not "confidential" — *actionable*. A source file is confidential and is not on this list; `.npmrc` is actionable and is.
2. **No agent has a legitimate reason to publish that file into a shared knowledge graph.** This is the precision half, and it is the half that keeps §3.1(c) satisfied.

A pattern that fails (2) must not be added even if it passes (1) — that is the rule that dropped `.claude/` from #10481's own suggestion list (§9.3 measures why) and that keeps `CLAUDE.md`, `appsettings*.json` and `docs/**` off the list despite each being a plausible secret carrier.

**The list is a table of names, not a taxonomy.** Grouped for the reader; the predicate treats them uniformly.

| Family | Patterns | Why admissible under (1) | Why admissible under (2) |
|---|---|---|---|
| Repository git metadata | `.git` | `config` carries a token in the remote URL for a token-authenticated remote; `hooks/` is executed by every git operation | Nothing under `.git` is ever a body someone files to the graph |
| Environment files | `.env*` | the conventional home of API keys and connection strings | an `.env` is a deployment input, not a document |
| Package-registry credentials | `.npmrc`, `.pypirc`, `.netrc`, `pip.conf`, `pip.ini` | registry and host tokens, in plaintext, by format | configuration inputs |
| Private key material | `*.pem`, `*.key`, `*.pfx`, `*.p12` | private keys and keystores | a private key in a shared graph is the failure this design exists to prevent |
| SSH private keys (extensionless) | `id_rsa`, `id_dsa`, `id_ecdsa`, `id_ed25519` | private keys with no extension to match on | as above. Note the **public** halves (`id_rsa.pub`) are deliberately **not** matched — measured (§9.2) |
| Agent-host configuration | `.mcp.json`, `settings.json`, `settings.local.json` | declare MCP servers, permissions, hooks and environment — reading may disclose keys, writing expands the tool surface | these are host configuration, not knowledge. **Bare `settings.json` is deliberately broader than that argument** — see the note below |

**Note on bare `settings.json` — the justification is widened, not the pattern narrowed.** The pattern matches *any* file named `settings.json` at any depth, not only the agent-host one it is argued for. Measured 2026-09-01: in this tree it matches exactly one file, `.vscode/settings.json`, and **zero** agent-host files — this repo carries `.claude/settings.local.json` but no `.claude/settings.json`. So today the pattern refuses one thing it is not aimed at and nothing it is.

I considered narrowing it to the host case and **rejected that**, on an architectural ground rather than a judgement call: the only way to express "the `.claude` one" is a two-component pattern like `.claude/settings.json`, and §8.2's fourth clause forbids a pattern from spanning a component boundary. That clause is not stylistic — it is what makes the verdict independent of *which* root containment matched, and §9.1 measures the failure it prevents (a session rooted at `.claude/worktrees/wt` where relative paths no longer contain the `.claude` component). Narrowing this one pattern would reintroduce root-order dependence for the whole predicate to spare one editor config file. That is a bad trade.

So the justification widens instead, and it should be read as: **any file named `settings.json` is refused, because the agent-host case cannot be distinguished from other applications' by name alone under a whole-component rule.** The cost is that a common filename is refused; `.vscode/settings.json` is counted as a false denial in §9.3 and in §3.1(c)'s tripwire rather than being excused. Dropping the pattern entirely was the third option and is rejected too: it would re-open `.claude/settings.json` — hooks and permissions, i.e. execution surface — to graph-controlled overwrite, which is one of the specific narrowings §10.2 credits this change with. Its absence from *this* tree is one repository at one moment (§9.4), not evidence the target is imaginary.

**Explicitly considered and rejected for membership**, each failing a named half of the bar:

| Candidate | Fails | Reason |
|---|---|---|
| `.venv/`, `node_modules/` (#10481 names `.venv/`) | (1) | third-party code, not credentials. The only credential-shaped things inside are already matched by `pip.conf` and `*.pem` |
| `.claude/` as a directory | (2) | **measured** (§9.3): the repo's own `.claude/` holds review write-ups, task bodies and a scratch design document — precisely the class of file that gets filed to DiVoid. Denying the directory would tax a live workflow. The two dangerous *files* inside it are named individually instead |
| `CLAUDE.md` | (2) | it is a published contract document; #114 and #420 live in DiVoid *because* someone filed them |
| `appsettings*.json`, `*.config` | (2) | routinely quoted and filed while debugging; the secret-bearing variants are deployment-specific and unpredictable |
| `*.db`, `*.db3` (the committed dev database) | (1) | graph content, not a credential. Also legitimately downloadable |
| `.docker/config.json`, `kubeconfig`, `*.jks` | (1) passes, (2) passes, **but none has been observed in this deployment's roots** | left off deliberately; adding patterns speculatively is the failure mode §3.1(c) warns about. Named here so a future addition has a recorded starting point |
| `.git-credentials` | (1) passes, (2) passes, **but not observed in this deployment's roots** (measured 2026-09-01: absent from the working tree) | Same standing as the row above, recorded separately because its absence is the one most likely to be misread as an oversight. **It differs from `.git` by a single hyphen and is therefore *not* matched by it** — the predicate matches whole components, so `.git` and `.git-credentials` are distinct names. Measured: **accepted**. It is git's plaintext credential store, so anyone assuming `.git` covers it is wrong; that assumption, not the omission, is the hazard this row exists to close |

### 8.2 The predicate

> A contained path is **refused** iff any component of its resolved form, taken relative to the root that containment matched, matches any listed pattern — where each component is first normalised by stripping trailing dots and spaces, both component and pattern are case-folded with the same `normcase` containment uses, and matching is glob-style with no path separator ever crossing a component boundary.

Four clauses in that sentence each answer a measured falsifier; none is decoration.

- **"resolved form"** — the junction case (§9.2). A raw-string check is defeated by an in-root junction.
- **"relative to the matched root"** — the worktree case (§9.2). An absolute-component check refuses everything in a session rooted under a `.claude` directory.
- **"stripping trailing dots and spaces"** — this is the one clause the *current* platform makes redundant, and it ships anyway. **Measured**: `os.path.realpath` on this interpreter already collapses `<root>\.git.\config` to `<root>\.git\config`, so the component match succeeds without the strip. That measurement **contradicts #10479 §9.3's reasoned claim** that trailing dots are "stripped by Win32 at open time but not by resolution". Two consequences: #10479 §9.3 is wrong on this point and is being corrected separately; and **the predicate must not depend on it**, for exactly the reason #10479 gave when it rejected the `\\?\` family syntactically — *the boundary must not depend on form- and version-dependent resolution behaviour*. Without the strip, a future interpreter that stops normalising turns a refusal into a **silent** acceptance. With it, the same change is inert. Per **#114 §0**'s bounce rule this is not a defensive check for a failure that cannot happen: the failure is measured to be one interpreter-version away, and the mitigation is one expression.
- **"no separator crossing a component boundary"** — a pattern must never be able to express a path fragment. This keeps the rule order-independent across multiple roots and keeps `.gitignore`, `.gitattributes` and `.github` allowed (measured, §9.2).

**Footnote — the redundancy is against two independent OS paths, not one, which strengthens the "ships despite being redundant" argument rather than merely restating it.** Implementation verification (2026-09-01, John) tried to isolate the third clause with a mutation test: delete `.rstrip(". ")`, force a `realpath` stand-in that does not collapse the trailing dot, and confirm the refusal still fires from the module's own stripping. The first version of that test monkeypatched only `os.path.realpath` — and it produced a false green: the test still passed with the strip deleted. The cause is that `_reject_if_sensitive` computes the relative path via `os.path.relpath(real, root)`, and on this interpreter `ntpath.relpath` calls `abspath()` internally, which on Windows resolves through `GetFullPathNameW` — the same Win32 API-level normalisation that collapses the trailing dot, invoked independently of `realpath`. A mutation test that stands in for only one of the two callers is therefore not testing what it claims to test; it has to route around both `realpath` and `relpath` (a pure-string stand-in for each) to actually isolate this module's own `.rstrip(". ")` from platform behaviour. Once both were replaced, the mutation went red as expected. This does not change the predicate or the shipped code — it means the "ships despite being redundant on this interpreter" claim above understates its own case: the strip is redundant against **two** independent OS-level normalisation paths on this interpreter, not one, which is a stronger argument for keeping it than the one originally written, not a weaker one. Filed as a footnote rather than a rewrite of the clause because the conclusion (ship it) is unchanged; only the confidence in *why* it doesn't cost anything today is sharper.

**Named limit on B3, since multi-root configurations are the case this repo's own history keeps getting wrong.** With the current list the verdict does not depend on *which* root containment matched, because no listed pattern names a directory that can appear as an ancestor of a configured root. That is a property of the list, not of the algorithm. **A future pattern that names such a directory — `.claude` is the concrete example — would make the verdict depend on root ordering.** Whoever edits the list owns that check; the list's own admissibility bar (2) happens to exclude the only example we have.

### 8.3 Error vocabulary

One new stable code.

| Code | Meaning | The caller's remedy |
|---|---|---|
| `path_denied_sensitive` | the path is inside a root but names a file the server refuses to read or write | **None within this tool.** There is no alternative path that works and no argument that changes the outcome |

**Why a new code rather than reusing `path_outside_root`.** #10479 §8.2 set the bar: a second code is justified only where the remedy genuinely differs. It differs maximally here. `path_outside_root` means *"choose a path inside the root"* — an action the caller can take. `path_denied_sensitive` means *"there is nothing to try."* Collapsing them would tell an agent to go looking for a different in-root spelling of a file it must not read, which is the laundering behaviour the message exists to forbid.

**The message must state, in this order:**

1. The **resolved** path and the component that matched. Without the component the caller cannot tell which rule fired, and for a junction or short-name input the resolved form is not derivable from the input string.
2. That this is a **deliberate refusal**, not a fault, and that retrying, re-spelling the path, or falling back to raw REST is the wrong response — carrying forward #10479 §8.2 point 3, whose reasoning applies here with more force because the remedy set is empty.
3. **Explicitly: do not copy the file to another name and upload the copy.** This is unusual for an error message and it earns its place — it is the only mitigation available for the laundering path (§10.1) against a *compliant* agent, it is free, and it converts a laundering instruction arriving from graph content into something that visibly contradicts a rule the tool just stated. An agent that follows it anyway has done something a reviewer can point at.

The message must **never** include file contents, and must continue to route through `make_error_content` rather than being hand-formatted (#6104 §5).

### 8.4 Configuration — there is none, deliberately

No environment variable, no override, no opt-out. Against Design Contracts §3:

- **No named operator and no named tuning event.** Nobody has asked to upload their `.git/config` to DiVoid.
- **No genuine environment difference.** `.env` and `.npmrc` mean the same thing in every deployment this package ships to.
- **An override would be the primitive it is defending against.** #10479 risk 13.6 records the shape: an operator "fixes" a rejection by widening the boundary. An override here is strictly worse than the root-widening case, because the natural way to write it — *disable the sensitive-path check* — turns one blocked call into a permanently open server.

**The escape hatch is intentionally out-of-band and attributable:** a human who genuinely needs a listed file's contents in the graph copies it, under a different name, by hand. That is the same act the error message forbids the *agent* from taking, and the asymmetry is the point — it is precisely the conversion of a silent one-call operation into a deliberate, attributable human act that the success criterion asks for.

---

## 9. Measured Behaviour — and What Would Falsify It

Per **#1220 §5**, every claim below is stated with the input that would break it. Rows marked **MEASURED** were run; rows marked **REASONED** were not.

**Measurement channel (per #10499).** Both probe scripts were written to disk with the `Write` tool — no shell layer anywhere between the source and the interpreter. Every backslash was constructed from `chr(92)`, and each constructed input asserted its own backslash count and its own distinguishing substring (`.git.` retaining its trailing dot) **before** being used. Environment: Python 3.14.2, Windows 11, fixture root under `C:\dev\claude\_scratch\`, sweep target the working tree at `C:\dev\claude\divoid`, 2026-09-01.

### 9.1 The falsifiers the predicate survives — MEASURED

| Input (relative to root) | Resolves to | Verdict | What it falsifies if it were wrong |
|---|---|---|---|
| `.git\config` | `.git\config` | **refused** | the baseline |
| `sub\.git\config` | `sub\.git\config` | **refused** | a rule anchored at the root's top level only. Nested git dirs are equally credentialed |
| `.git\..\.git\config` | `.git\config` | **refused** | a rule applied before `..` collapsing |
| `fakedir\config`, where `fakedir` is a **directory junction** to `.git` | `.git\config` | **refused** | **the raw-string check.** The input has no `.git` component; the resolved path does. Junctions need no elevation on Windows |
| `.git.\config` (trailing dot component) | `.git\config` | **refused** | a name match that trusts the input spelling. Note `realpath` already normalises this — see §8.2's third clause for why the design does not rely on that |
| `.git \config` (trailing space component) | `.git\config` | **refused** | as above |
| `<8.3 short form of .git>\config` — measured as `GIT~1` | `.git\config` | **refused** | a rule that runs before short-name expansion. 8.3 generation is enabled on this volume (#10479 §9.2 measured `DIVOID~1` → `divoid-frontend`) |
| `.env`, `certs\server.pem`, `.claude\settings.json` | themselves | **refused** | the non-`.git` families |
| `.claude\agents\jenny.md` | itself | **allowed** | a rule that denies `.claude` wholesale — see §9.3 |
| `.claude\worktrees\wt\docs\note.md`, with the **worktree as the root** | itself | **allowed** | **absolute-component matching.** Measured directly: absolute matching flags the worktree root itself, so it would refuse *every* path in such a session |
| `.github\workflows\ci.yml`, `src\.gitignore`, `src\.gitattributes` | themselves | **allowed** | a `.git`-prefix match instead of a whole-component match. All three are common; refusing them would be a visible, frequent false denial |
| `tools\id_generator.py` | itself | **allowed** | an `id_*` glob. It would match this file — which is why the four SSH key names are listed literally |
| `keys\id_rsa.pub` | itself | **allowed** | over-matching on the harmless public half |
| `docs\environment.md`, `docs\gitignore-notes.md`, `monkey.py` | themselves | **allowed** | substring matching anywhere in the rule |

### 9.2 The falsifier the predicate does **not** survive — MEASURED

| Input | Resolves to | Verdict | Consequence |
|---|---|---|---|
| `innocent.md`, created as a **hardlink** to `.git\config` | `innocent.md` | **allowed** — and `open` reads the git config's 59 bytes | `realpath` does not resolve hardlinks, because a hardlink has no target: both names are equally real. On NTFS `mklink /H` needs no elevation |

This is #10479 §10.8 carried forward, and it is worth stating that it is now doing more work than it was: under containment alone a hardlink could only reach a file the agent could reach anyway. Under this design it is a **bypass of the refusal**. It is the cheapest laundering technique available and it is not closable without comparing volume and file index, which is disproportionate here. Named, not argued away.

### 9.3 Friction on the legitimate path — MEASURED

The claim under test is the one the brief cares most about: *does this tax the path the tool exists to serve?* The measurement is a sweep of the predicate over the entire working tree.

| Quantity | Value |
|---|---|
| Files under `C:\dev\claude\divoid` | 51,539 |
| Refused by the predicate | 4,781 (9.28%) |
| Refused **under `.git`** | 4,772 |
| Refused **elsewhere** | 9 |

The nine, in full:

| Pattern | Path |
|---|---|
| `settings.local.json` | `.claude\settings.local.json` |
| `settings.json` | `.vscode\settings.json` |
| `*.pem` | `divoid-mcp\.venv\Lib\site-packages\certifi\cacert.pem` |
| `*.pem` | `divoid-mcp\.venv\Lib\site-packages\pip\_vendor\certifi\cacert.pem` |
| `.mcp.json` | `divoid-mcp\examples\.mcp.json` |
| `.env*` | `frontend\.env.example` |
| `.env*` | `frontend\.env.local` |
| `settings.local.json` | `frontend\node_modules\nanoid\.claude\settings.local.json` |
| `settings.local.json` | `node_modules\nanoid\.claude\settings.local.json` |

**Reading the result honestly.** The nine split into three groups, and the distinction between the second and third is the one that matters for §3.1(c)'s tripwire.

| Group | Count | Files | Assessment |
|---|---|---|---|
| **Correct refusals** | 4 | `.claude\settings.local.json`, `frontend\.env.local`, and the two vendored `node_modules\nanoid\.claude\settings.local.json` | live host config or a live env file. Refusing them is the design working |
| **Over-matches — fail bar (1), cost nothing** | 2 | the two `certifi\cacert.pem` bundles | a CA bundle is a set of **public** certificates, not a credential, so `*.pem` over-reaches here. It costs nothing because no agent files a vendored CA bundle to the graph, so bar (2) still holds and there is no friction to pay |
| **Plausible false denials — fail bar (2)** | **3** | `divoid-mcp\examples\.mcp.json`, `frontend\.env.example`, `.vscode\settings.json` | files an agent could legitimately want to pass to these tools. **These are the three that count against the tripwire** |

**On the three.** `divoid-mcp\examples\.mcp.json` is a committed example registration. `frontend\.env.example` is the sharper case: it fails **both** admissibility bars — it holds placeholders rather than a live credential, and it is committed precisely so that people read it. `.vscode\settings.json` is refused by the deliberately-widened bare `settings.json` pattern (§8.1's note).

Cost when any of them is hit: the agent gets `path_denied_sensitive` and reads the file with the harness `Read` tool instead — adequate for three short files, and inadequate only for the large-body case none of them is.

**I judge that acceptable and I am naming all three rather than carving any of them out.** A name-exception for `.env.example`, or a location carve-out for `examples/`, is the location-allow-list family §12.5 rejects and the kind of surface Design Contracts §4 asks to delete. If the count genuinely crossed §3.1(c)'s threshold the remedy would be **dropping the offending pattern**, not special-casing a name — and per §3.1(c) it has not crossed, because the threshold is per-pattern and these three are attributable to three different patterns.

**Correction of record.** An earlier version of this paragraph called `.mcp.json` *"the single plausible false denial in the tree"* while the table above it already listed `.env.example`. The prose undercounted its own data, which matters more than an ordinary inaccuracy would: §3.1(c) sets a tripwire on this count, and **a tripwire whose counter is wrong cannot fire.** Found in QA review #10773.

**The measurement that killed a pattern #10481 proposed.** Sweeping the repo's own `.claude/` directory shows it holds review write-ups (`pr57-review.md`, `jenny-pr57.md`), task bodies (`task-251.md` … `task-384-restore.md`) and `scratch/embeddings-v2-sql-composition-doc.md` — documents whose natural destination *is* DiVoid. Denying `.claude/` as a directory, as #10481's option list suggests, would have taxed a live workflow. Two files inside it are named individually instead.

**Sibling checkout control.** The same sweep over `C:\dev\claude\divoid-frontend` (33 files) refuses 30, all under `.git`, and none outside it.

### 9.4 What the friction measurement structurally cannot reach

Stated so the next person sweeps the complement rather than repeating me (#1220 §5, layer-5 discipline).

- It measures **files present in the tree**, not **calls actually made**. A file that no caller ever passes to `set_content` contributes a false positive to the count and no friction in practice; a file class that callers use constantly but that does not exist in this tree contributes nothing to the count and could be pure friction. The sweep therefore establishes an **upper bound** on friction in this repo, not the actual rate.
- It is **one repository at one moment**. A repository with committed test certificates, or a Python project keeping `pip.conf` in-tree, would score worse.
- It says nothing about the **write** side, for which no comparable population exists — nobody has a directory of "paths people download nodes into". The write-side friction claim in §6 is **REASONED**, not measured, and it is the weaker of the two.

---

## 10. What This Design Does **Not** Protect Against

Ordered by how much each matters. #10479 §10's items all remain in force; only the ones this change moves are restated.

**10.1 — Copy-then-upload laundering. The largest remaining hole, and it is unclosable by this mechanism family.**
An agent with a shell can copy a listed file to an unlisted name inside the root and upload the copy. **Measured** in the strongest available form: a hardlink to `.git/config` is accepted and reads the credential (§9.2); a plain copy is not even a resolution question. Nothing name-based reaches this, and a mechanism that did would have to inspect content (§12.4, rejected).

What the design buys against it is bounded and worth stating precisely: the attack changes from *one call to a tool the agent uses dozens of times a day* to *a shell command whose only purpose is to relocate a credential file, followed by an upload* — visible in the transcript, in scope for the harness boundary guard, and in direct contradiction of a rule the tool stated in the refusal message (§8.3 point 3). **That is the whole of the gain. It is a raised cost and a visibility change, not a barrier.**

**10.2 — Overwrite-in-root, narrowed but open.**
#10479 §10.3 said graph-controlled bytes can be written over any in-root file. That is now false for `.git/**`, `.mcp.json`, `settings.json` and `settings.local.json`, and **still true for everything else** — source files, `CLAUDE.md`, `docs/**`, and any existing hook script that a settings file already references. The narrowing covers the paths that most directly become execution or tool surface; it does not bound the primitive.

**10.3 — Low recall. The design's defining accepted limit.**
The refusal covers files whose **names** are known to carry credentials. It does not and cannot cover a secret in a file with an ordinary name: a token pasted into `notes.md`, a connection string in `appsettings.Development.json`, a password in a design document. **That class is large and it exists in the wild.** The failure is silent — nobody learns that a secret left — whereas the opposite failure, a false denial, is loud and immediately actionable. §12.1 explains why the design nonetheless accepts the silent axis.

**Where a reviewer should actually attack — corrected after QA #10773.** This section previously nominated itself as the first target. That was wrong, and wrong in an expensive direction: the deny-list-incompleteness critique this limit invites is already answered by **§10.7** (a gap in a fence and an alarm that does not cover every room are different failures), so a reviewer who starts here spends the pass on the design's best-defended surface. Both findings that QA did land came from one level down — **not "is a name-based list complete?" but "is this the right named list?"** The productive targets, in order:

1. **§8.1's membership table** — is each pattern's admissibility argument sound, and does any listed pattern match something it does not argue for? Both QA findings were here: `.git-credentials` (a credential file the `.git` pattern does *not* catch, contrary to the natural assumption) and bare `settings.json` (broader than its own justification).
2. **§9.3's classification** — is a file in the "correct refusal" or "over-match" group actually a false denial? That is the input to §3.1(c)'s tripwire, and this document has already had that count wrong once.
3. **§10.1's laundering gain** — is the visibility argument worth what it claims? It is the least measurable claim in the document.

**10.4 — An agent that is itself the adversary.**
Unchanged from #10479 §10.2 and restated because #10481 asks for it explicitly: **this is not a jail.** An agent that has decided to exfiltrate can copy, encode, chunk, or simply read the file and retype its contents through `content`. Every claim in this document is scoped to B1 — a *steered or mistaken* agent that follows the tools' stated rules. If B1 is false, the design's value drops to approximately zero and nothing in §12's alternatives would have done better.

**10.5 — A degenerate root.**
If a root is configured *inside* a listed directory — root = `<repo>/.git` — the relative path never contains the listed component and nothing is refused. Same class as #10479 §10.7 (the sanity gate bounds a bad root's breadth, not its location) and closed by the same non-mechanism: it is named, not guarded.

**10.6 — POSIX case sensitivity.**
`normcase` is identity on POSIX, so `.GIT` would not match. On POSIX `.GIT` is not the git directory, so this is not a bypass — but a POSIX filesystem *can* hold a directory genuinely named `.Env`, and it would not be refused. Narrow, and stated rather than implied by the Windows-shaped measurement table above.

**10.7 — The list is not, and cannot be, complete.**
This is the standard deny-list critique and it applies in full. #10479 §8.4 rejected a *system-directory blocklist* on exactly this ground, and the distinction matters: there, the blocklist was proposed as the **containment boundary**, so incompleteness would have made the boundary claim false. Here containment already exists and holds; this list is a second, narrower refusal whose claim is bounded to the names it enumerates. **A gap in a fence and an alarm that does not cover every room are different failures.** That distinction is the design's justification and also its limit — do not read the list as a claim about secrets in general.

---

## 11. Cross-Cutting Concerns

**Security.** Fail-closed is unchanged: every branch that cannot produce a verdict is a rejection. The new step adds no branch that can yield "allow" on an unhandled condition — an unmatched component set is the *normal* allow path, reached only after containment already succeeded.

**Secrets (C2).** The refusal message echoes the resolved path and the matched component — names, never contents. This is strictly less exposure than the existing `file_not_found` branch, which already echoes `path!r`.

**Observability (C3).** Refusals log at INFO to stderr with the code, as the existing gate refusals do. `path_denied_sensitive` in the MCP stderr log is the operational signal to watch after rollout: a refusal naming a file that *is* legitimately a document body means the offending **pattern should be removed**, not that an override should be added (§8.4).

**Error handling.** No new envelope, no new exception type, no new mapping. `InvariantViolation` → `make_error_content` → `{"isError": True, …}`.

**Performance.** A bounded number of glob comparisons over the components of one already-resolved path, on a call that is already doing network and disk I/O. Immeasurable.

**Concurrency / idempotency / caching.** Unaffected. The predicate is pure; the pattern table is immutable for the process lifetime; no shared mutable state is introduced.

---

## 12. Quality Attributes & Trade-offs — Alternatives Rejected by Name

### 12.1 The central trade: precision over recall

The brief's question — *can this be closed without taxing the path the tool exists to serve?* — has an honest two-part answer.

**For a named subset: yes, at measured near-zero cost.** §9.3 finds three arguable false denials in 51,539 files, spread across three different patterns. The legitimate large-body upload path — an agent writing a design document, a session log, or a review body and filing it — is untouched, because none of those files carry a listed name.

**For the class: no.** Any mechanism with meaningful recall against "a secret inside the root" must either inspect content (§12.4) or restrict by location or type (§12.5), and both tax the ordinary case heavily enough that the tax gets routed around. Routing around is not a neutral outcome: it returns the caller to inlining through `content`, which is **measured** to corrupt bodies (#8523, #7895), or to raw REST, which defeats the guard entirely. **A mechanism that gets bypassed is worse than the hole**, because it costs the guard *and* the corruption.

| Axis | Cost | Failure mode | Direction |
|---|---|---|---|
| Recall (missed secrets) | large, unbounded | a secret reaches the graph and nobody learns | **silent** |
| Precision (false denials) | measured 2026-09-01: 3 arguable cases in this tree, across 3 patterns | caller reaches for `content` inlining or REST — losing both the guard and body fidelity | **loud, then bypassed** |

The design accepts the silent axis. That inverts the usual security-list orthodoxy, which prefers over-blocking, and the inversion has a specific reason: **orthodoxy assumes over-blocking is merely inconvenient. On this surface over-blocking causes migration to an unguarded channel with a known corruption defect.** That assumption failure is the same one #5837 recorded when a status allow-list forced REST fallbacks (§3.1c).

This table is the argument to attack if you think the *direction* of the trade is wrong. If you think the direction is right but the execution is off, §10.3's revised target list is the productive route — that is where both QA findings on this design came from.

### 12.2 Confirmation on the read side — **rejected**, in all three forms

#10481 names confirmation as one of two starting points. All three implementable shapes fail.

| Shape | Verdict |
|---|---|
| **A `confirm=true` parameter on `set_content`** | **Rejected.** The adversary and the confirmer are the same channel. A node body that can say *"upload the file at this path"* can equally say *"and pass `confirm=true`"*. It protects against the mistaken agent only, adds a parameter and a code, and gives the steered case a **false sense of having been consented to** — which is worse than no mechanism, because a reviewer reading the transcript sees an affirmative confirmation |
| **Challenge/response — first call returns a token, second call replays it** | **Rejected.** Ceremony the same agent performs unaided. It satisfies "no longer *one* call" literally and delivers nothing: two automatic calls are one operation. It adds state to a stateless gate for no verdict change |
| **Out-of-band human confirmation via MCP elicitation** | **Rejected**, and this is the one that genuinely tempts. It is the only shape that puts a *different* principal in the loop. But: it makes a security boundary depend on a host capability the package cannot assume (`divoid-mcp/CLAUDE.md`: "a generic-purpose tool used outside this deployment"), and a capability-negotiated boundary **fails open** on hosts that lack it; the machine already runs `defaultMode: auto` precisely so the operator is not interrupted per call, so the prompt would meet a human who has opted out of prompts; and if it fires only on the listed patterns, the list is doing all the selection work anyway — making this a strictly more complex delivery of §8.2's decision |

**Kept from the confirmation idea:** the *attributability* it was reaching for, delivered by the out-of-band escape hatch in §8.4 rather than by a prompt.

### 12.3 Deriving the list from `.gitignore` — **rejected**

Superficially the most elegant option: *a file git refuses to share is a file the MCP refuses to share*, and it is DRY with a list humans already maintain (Design Contracts §2). Rejected on four counts:

1. **It fails open, silently.** An `.env` that someone forgot to ignore sails through. The mechanism's coverage becomes a property of build hygiene.
2. **It does not cover the primary target.** `.git/` is outside the work tree, so ignore rules say nothing about it. A special case would be needed anyway — and it is the case that matters most.
3. **It makes the boundary depend on repository state at call time**, i.e. non-deterministic and hard to test, versus a frozen table.
4. **It costs a dependency or a subprocess** — a `pathspec`-style matcher or a `git check-ignore` invocation — in a package that currently has neither, with a fail-open story when git is absent or the root is not a repository.

### 12.4 Content inspection (entropy / token-prefix scanning) — **rejected**

Scan the bytes before upload and refuse what looks like a secret. Rejected:

- **It breaks the tool's stated purpose.** `set_content(path=…)` posts bytes verbatim, including binary (#6597's sibling case). Any content rule must either exempt binary — leaving the obvious carrier open — or refuse it.
- **False positives land on exactly the documents this graph is for.** A design document about credential handling contains credential-shaped strings; #10472 itself quotes a remote URL with `TOKEN` in it. Refusing to file security findings to the security graph is a self-defeating outcome.
- **It is the precise shape #1220 §5 was written about** — the Pooshit.Http incident, where a well-argued lexical rule over names was falsified by nine counterexamples in one reviewer pass. A rule over arbitrary bytes is strictly harder.

### 12.5 Location or type allow-lists — **rejected**

*Only allow uploads from `docs/`*, or *only allow `*.md`, `*.txt`, `*.png`*. Rejected: these are the mechanisms most certain to be routed around. Agents legitimately upload from all over the tree, `download_content` exists precisely for arbitrary binary, and an extension allow-list is client vocabulary in the sense invariant 6 actually forbids. This is the family §12.1's trade-off table is warning about.

### 12.6 Holding the pattern list in DiVoid as a node — **rejected**

Tempting because it makes the policy data rather than code, shareable across clients, and editable without a release. Rejected on one decisive ground: **the threat model's premise is that graph content is untrusted.** A policy fetched from the graph can be edited by whatever can edit the graph, so the guard's own definition becomes attacker-influenced — the exact inversion of #10472's reachability finding. Secondary: it adds a startup fetch with a fail-open-or-closed dilemma, on a server that currently starts with one config read.

### 12.7 Leaving it to the harness hook — **rejected**

#10479 §14 C-6 already binds this: the server fix must be complete on its own, because the hook does not exist for non-Claude-Code hosts and can be disabled by anything with configuration access. Two additions specific to this change: the hook adjudicates the *caller-supplied* string in a different process, so it cannot see through the junction case measured in §9.1 (C-4 requires it to deny rather than guess); and this change now refuses writes to `settings.json`/`settings.local.json`/`.mcp.json`, so server-side and harness-side reinforce each other rather than substituting.

### 12.8 Attribute summary

| Attribute | Effect |
|---|---|
| Security | Named credential and host-configuration files are refused in both directions. Recall against "secrets" generally is low and stated (§10.3) |
| Maintainability | One predicate, one table, zero new modules, zero new call sites, zero configuration. The table has an admissibility bar (§8.1) so edits are decidable rather than a matter of taste |
| Performance | Immeasurable |
| Compatibility | No change for any path the list does not name. Measured: 9 files in 51,539 outside `.git` |
| Testability | The predicate is pure and table-driven; §9.1 and §9.2 double as the vector table, including the row that must stay **allowed** |

---

## 13. Risks & Mitigations

| # | Risk | Likelihood | Mitigation |
|---|---|---|---|
| 13.1 | Implementer matches on the **raw** path instead of the resolved one | Moderate | §9.1's junction row is the falsifying input and must appear as a required test, not a note |
| 13.2 | Implementer matches components of the **absolute** path | **High — it is the shorter code** | §9.1's worktree row. Measured to refuse *every* path in a worktree-rooted session. Required test |
| 13.3 | Implementer uses a substring or prefix match rather than whole-component glob | Moderate | `.github`, `.gitignore`, `.gitattributes` must stay allowed; all three are in the required vector table |
| 13.4 | Implementer "tidies" the four SSH names into `id_*` | Moderate | `tools\id_generator.py` is the falsifying input and is in the table as an **allowed** row |
| 13.5 | Implementer drops the trailing-dot/space normalisation because the platform already handles it | Moderate — the measurement says it is redundant *today* | §8.2's third clause states the reason. The test must assert the **verdict**, so it stays green either way; the guard's value is that a future platform change cannot silently flip an acceptance |
| 13.6 | Someone later adds an override knob in response to a false denial | Low but severe | §8.4. The README paragraph must say the remedy for a false denial is to **remove the pattern**, never to add a bypass |
| 13.7 | The list grows speculatively until it starts causing false denials, which causes bypass | Moderate over time | §8.1's two-part admissibility bar, and §11's instruction to treat a logged refusal on a legitimate document as a signal to *shrink* the list |
| 13.8 | Merged but not reinstalled, so nothing changes in practice | Moderate | C8 — pinned non-editable git install. Version bump → merge → `pip install --force-reinstall` → full host restart. State it in the PR body |
| 13.9 | The refusal is read as a tool defect and routed around via `curl` | Moderate | §8.3's message requirements, points 2 and 3 |

---

## 14. Migration / Rollout

1. Merge. No data migration, no schema change, no deprecation window (private repo, atomic deploy — Design Contracts §5).
2. Bump `__version__` (behaviour-changing minor).
3. Reinstall the pinned operational MCP per `divoid-mcp/CLAUDE.md` and **fully restart the MCP host**. Until then the change is inert (13.8).
4. Watch stderr for `path_denied_sensitive`. A refusal naming a file that is genuinely a document body means **remove that pattern** and re-measure — it does not mean add an override.

---

## 15. Open Questions

**Q1 — Does the write side belong in this task?** §6 folds it in, because excluding it costs code rather than saving it. **For the coordinator to confirm before John starts.** It does not change the design's shape; it changes what the PR body claims.

**Q2 — three measured plausible false denials, one per pattern; accept all three or drop a pattern.** The 2026-09-01 sweep found `divoid-mcp/examples/.mcp.json` (`.mcp.json`), `frontend/.env.example` (`.env*`) and `.vscode/settings.json` (`settings.json`) — see §9.3 for the classification and §3.1(c) for why the per-pattern split means the tripwire has not fired. My call is to **accept all three**. If Toni disagrees, the remedy for any one of them is **dropping that pattern from the list** — *not* carving out `examples/`, `*.example`, or `.vscode/`, which is the location-allow-list family §12.5 rejects.

Of the three, `.env.example` is the one I would expect an objection to, because it fails **both** admissibility bars rather than one: placeholders, not a live credential, and committed to be read. I still would not drop `.env*` over it — that pattern is the highest-value entry on the list and `.env.local` in the same directory is exactly what it is for — but that is the trade being accepted, and it should be accepted knowingly rather than by omission. *(An earlier version of this question named only `.mcp.json` and called it "the single" case, which understated what Toni is being asked to rule on. Corrected after QA #10773.)*

**Q3 — Should `path_denied_sensitive` refusals be counted somewhere durable?** §11 says watch stderr, which is what #10479 said about `path_outside_root`, and stderr is not read by anyone in practice. I have **not** designed a durable signal: a node-per-refusal writes attacker-observable data into the graph the attacker reads, and no cheaper option earns its place. Raising it because "watch the logs" is the weakest sentence in this document and I would rather name it than let it pass as a plan.

**Q4 — Does a hardlink-capable laundering path deserve its own follow-up task?** §9.2 measures it working. My recommendation is **no**: comparing volume and file index is disproportionate, and §10.1 makes the honest claim that the copy path is unclosable anyway — a hardlink-specific fix would close the cheapest instance of an open class and might read as closing the class.

---

## 16. Implementation Guidance for the Next Agent

Ordered. No code below by design; each item is an architectural unit.

**Milestone 1 — the pattern table in `paths.py`.** A module-level immutable table of the names in §8.1, alongside the existing rejected-prefix table so the two policy surfaces sit together. It carries no comments — the rationale lives here and in the module docstring convention the file already uses (Jenny's CF-1 in review #10494 removed comments from this module; do not reintroduce them).

**Milestone 2 — step 6 inside `gate()`.** Placed where the containment loop currently returns the resolved path: at the point a root has matched, compute the components relative to *that* root, normalise each by stripping trailing dots and spaces, case-fold with the same `normcase` the containment comparison uses, and glob-match against the table. On a match raise `InvariantViolation("path_denied_sensitive", …)` with the message content specified in §8.3. On no match return the resolved path exactly as today.

Do **not**: match the raw path; match components of the absolute path; use substring or prefix matching; collapse the four SSH names into a glob; introduce a separate helper module (the predicate has one call site — extraction would be an indirection, per Design Contracts §4).

**Milestone 3 — unit coverage** in `divoid-mcp/tests/unit/test_paths.py`, which already covers the gate. Every row of §9.1 is a required case, **including the rows whose expected verdict is `allowed`** — those are the ones that pin the design against over-matching, and an implementation that refuses them is broken in the direction that causes bypass. The junction row and the worktree-root row are the two most important assertions in the suite (risks 13.1, 13.2); both need a real on-disk fixture, not a mocked `realpath`. Add the hardlink row from §9.2 as an **expected-allowed** case with a comment-free test name that says it is a known limit, so a future round cannot "fix" it silently and cannot mistake it for a regression.

**Milestone 4 — tool-level coverage** in `test_set_content.py` and `test_download_content.py`: `_execute` called directly with a denied in-root path returns the `path_denied_sensitive` envelope, **opens no file, and issues no HTTP call**. The no-HTTP assertion is the one that pins the ordering; without it the test passes even if the check migrates below the network call.

**Milestone 5 — smoke coverage** per #6104 step 4 (`tests/smoke/`, not pytest — print PASS/FAIL, exit non-zero): one refused read, one refused write, one accepted in-root round trip proving the ordinary path still works.

**Milestone 6 — tool descriptions.** Both `_TOOL_DESCRIPTION` strings gain a sentence naming the new code, stating that the refusal is deliberate, that no alternative path exists, and that copying the file to another name is not the remedy. #6104 §1: this string is the model-facing contract and is doing real work — an agent that never learns the rule will keep proposing denied paths, and every such proposal is a refusal in the log that looks like friction.

**Milestone 7 — version and README.** Bump `__version__`. Add one paragraph to `divoid-mcp/README.md` beside the existing `DIVOID_MCP_FILE_ROOT` paragraph, stating the refusal, that it is not configurable, and that the remedy for a false denial is to remove the pattern in a PR — never to add a bypass (risk 13.6).

**Do not add** in this PR: a configuration knob (§8.4), a confirmation parameter (§12.2), content inspection (§12.4), an `examples/` carve-out (Q2), a hardlink defence (Q4), or any change to containment (§2).

---

## 17. Design Contracts §5 Pre-Design Checklist

**KISS / DRY / YAGNI**
- [x] No new type mirroring an existing one — no new types. `InvariantViolation` and `make_error_content` are reused unchanged.
- [x] No new abstraction with one implementation — no new module, no interface, no helper. The predicate has one call site, so extraction would be an indirection (§4 delete/merge/inline).
- [x] No element justified by "we might need X later". The `.docker/config.json` / `kubeconfig` / `*.jks` family was explicitly **not** added, on the grounds that it has not been observed in this deployment's roots (§8.1).
- [x] No deprecation period, feature flag, compatibility shim, or transition window.
- [x] **DRY math:** the predicate is ~12 lines at **one** site inside `gate()`; both tools reach it through the existing single call. `12 × 1 = 12`, below the ~15–20 threshold (#1267) — so extraction is correctly **not** performed, and the number is quoted rather than paraphrased.
- [x] **Code Contracts #114 §0 — "no defensive checks for failures that can't happen" — considered, and one override is grounded in measurement.** The trailing-dot/space normalisation (§8.2) looks like the anti-pattern: the platform is **measured** to normalise these already, so the strip changes no verdict today. It ships because the measurement also establishes that the predicate's correctness *currently depends on a platform behaviour*, and the failure direction if that behaviour changes is a **silent acceptance**. That is a failure that can happen — one interpreter version away — and the mitigation is one expression. Per §0's bounce rule the override is grounded in measurement, not paraphrase. Everything genuinely impossible was left out: no nil-guarding, no retries, no fallback, no shim.

**Existing systems first**
- [x] Audited. The gate already exists, is already called before all I/O at both sites, and already returns the resolved path — every structural precondition this design needs is present. This is a new rejection reason on an existing predicate, not a new layer.
- [x] `errors.py` already provides the exception and envelope — reused.
- [x] No new persisted data, no new configuration surface.
- [x] No transitive-dead-code chain: the new code has two live callers on the critical path, and the new error code has a specified consumer (the model, via the tool description).

**Configurability**
- [x] **No new knob.** §8.4 states the reasoning against all three of Design Contracts §3's justifications, plus the specific reason an override here is more dangerous than the root-widening case it resembles.
- [x] No "telemetry-then-tune" compound. The pattern table is a `const`-equivalent in code; a change to it is a PR, which is the intended review path.
- [x] No magic numbers introduced.

**Less is better**
- [x] Delete / merge / inline run on every element. **Deleted along the way:** a scoped `.claude/**` rule (measured unnecessary and harmful, §9.3), a `hooks` pattern (subsumed by `.git` and by the settings files), an `id_*` glob (measured over-matching), a `.venv/` pattern (#10481 suggested it; fails admissibility bar 1), an `examples/` carve-out (Q2), a second error code for the read/write distinction, a configuration override, and a separate predicate module.
- [x] Trade-offs named explicitly with both halves priced: §12.1's recall-vs-precision table, §9.3's measured friction, §10.3's named accepted limit.
- [x] Radical-clean chosen where no consumer dictates otherwise: one predicate, one table, one code, both directions.
- [x] Reader / scope inventory explicit: the gate's two callers are enumerated by name, and both are unchanged.

**Document discipline**
- [x] Code Contracts (#114) and Design Contracts (#1136) cited as load-bearing.
- [x] Out-of-scope items listed explicitly (§2), not merely absent.
- [x] No multi-paragraph rationale for things that obviously stay.
- [x] **Supersedes nothing.** #10479 remains live and correct; this design extends it and closes its §10.1. No predecessor banner is warranted, and #10479 must not receive one.
- [x] Every containment-adjacent claim carries its falsifier (§9), the limits are named rather than argued away (§10), and the axis that fails **silently** is stated in both §10.3 and §12.1 per #1220 §5's direction-of-error rule.
- [x] **Pointer form:** every reference to code is by symbol, file, or behaviour — no `file:line` citations anywhere in this document (#1220 §5; three citations expired during #10479's review, one within minutes).

**Data deliverables** — not applicable. No SQL, migration, or backfill.
