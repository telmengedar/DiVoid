# Architectural Document: Filesystem-Path Containment for `divoid-mcp`

**Status:** design, ready for implementation
**Author:** Sarah (software architect), 2026-09-01
**Source task:** DiVoid **#10473** (severity 4, CRITICAL) · **Source finding:** DiVoid **#10472** (measured PoC, linus-security-redteam)
**Target branch:** `fix/mcp-path-containment`
**Standards applied:** Design Contracts **#1136** (load-bearing) · Code Contracts **#114 §0** (load-bearing) · `divoid-mcp/CLAUDE.md` invariants 1–6 · Tool anatomy **#6104** · Inbound-proposal ruling **#435** · Falsifiability addendum **#1220 §5**

> **Post-merge correction, 2026-09-01.** The repo-map reconcile read this document against the shipped code at `e17a358` and found four disagreements. All four shared one root cause: **the document described the single-root default and silently generalised it to the multi-root case that `DIVOID_MCP_FILE_ROOT` exists to enable.** Corrected here in §5, §8.1, §9.1, §9.2, §11, §12.1, §12.3, §14 C-1/C-2 and §18, and a required multi-root test case added to §18 Milestone 5. The design's decisions are unchanged; what changed is that claims true for one root are no longer stated as if true for N. Where a claim is a property of the **default** rather than of the design, it now says so.
>
> The document also carried a hand-maintained tool count, which had gone stale. Counts have been replaced with the rule that produces them (#1176).

---

## TL;DR

**What.** Two `divoid-mcp` tools take a caller-supplied filesystem path and pass it straight to the OS: `divoid_download_content` writes node bytes to it (and creates any parent directories), `divoid_set_content` reads it and uploads the bytes into the shared DiVoid graph. There is no validation of any kind. Both were proven exploitable off-workspace (#10472).

**How.** One new module — `divoid-mcp/src/divoid_mcp/paths.py` — holds a **root directory list frozen at process start** and a single **path gate** that every caller-supplied path must pass. The default root is the **directory the server process was launched in** (measured: the MCP host sets this to the session's project checkout), overridable by `DIVOID_MCP_FILE_ROOT` for hosts where that is not meaningful. The gate rejects extended-length/device path prefixes syntactically, resolves the path through `os.path.realpath`, compares it **component-wise and case-folded** against the roots, and hands back the *resolved* path — which is then the path actually opened. Rejections raise `InvariantViolation` with the stable code `path_outside_root`, surfacing through the existing `make_error_content` envelope. The gate runs **inside `_execute`, before the HTTP call and before any disk touch**.

**Two deliberate divergences from the finding's proposed remedy** (per #435 the remedy is not authority): the **byte cap is excluded** — it is a resource-bound concern belonging to the DiVoid backend, not a containment concern — and the **default root is *not* `C:\dev\claude`**; that root is machine-specific, unshippable in a package used outside this deployment, and measurably too broad (it would leave `C:\dev\claude\divoid-frontend` and every sibling checkout in reach of this session).

**Both `path` parameters stay.** Removing them does not remove a risk, it removes the only lossless byte channel the tools have and pushes the data back through the model's token stream — which is the exact defect `path` was added to fix (#8523 / #7895) and is impossible for binary content (#6597).

---

## 1. Problem Statement

`divoid-mcp` exposes two tools whose behaviour is steered by a caller-supplied filesystem path, with no bound on where that path may point:

| Tool | File | Direction | Current behaviour |
|---|---|---|---|
| `divoid_download_content` | `tools/download_content.py` → `_execute`, the `makedirs` + `open(…, "wb")` block | **write** | `os.makedirs(dirname(abspath(path)), exist_ok=True)` then `open(path, "wb")` |
| `divoid_set_content` | `tools/set_content.py` → `_execute`, the `open(…, "rb")` inside the `path is not None` branch | **read** | `open(path, "rb").read()`, bytes POSTed to the node verbatim |

Validation today is limited to `node_id >= 1`, exactly-one-of-`content`-or-`path`, and non-empty-string. No containment check, no traversal rejection, no symlink handling.

Toni, 2026-09-01, on discovering this:

> *"you can give the mcp an arbitrary path and it just downloads data there ... would it write to system if you told it to?"*

and on filing it:

> *"file that as critical to the divoid project … we should close that gap as soon as possible, especially uploading arbitrary files is serious, but writing to arbitrary paths is also uncool"*

The read half is the one he named first and it is the more serious of the two: DiVoid is a **shared, multi-agent** substrate. `divoid_set_content(id, path=…)` is a *read-anything-then-publish* primitive — it lifts any file the account can read into a graph that other agents read, silently and with no local disk footprint.

**Success criteria.** After this change, no sequence of arguments to the `divoid-mcp` tool surface can (a) cause a file outside the session's permitted root to be uploaded into node content, or (b) cause node bytes to be written outside that root, or cause directory trees to be created outside it. Legitimate in-workspace use — the entire reason both parameters exist — is unaffected.

### 1.1 What I took from #10472 / #10473, and what I discarded

Per the ruling in DiVoid **#435**, a proposed solution arriving inside an inbound report is evidence-free authority and is discarded; the report's *observations* are taken in full. Applying the diagnostic — *which sentences would still be true if the author knew nothing about our internals?*:

| Taken (report) | Discarded and re-derived (remedy) |
|---|---|
| The exact two call sites and line numbers | The containment mechanism |
| PoC results: off-workspace write, `C:\Windows\Temp` write, `C:\ProgramData\…` tree creation, `C:\Windows\win.ini` → graph upload | Where the root comes from (`DIVOID_MCP_FILE_ROOT` env, comma-separated) |
| Baseline: standard non-admin token; MCP process inherits it | The default root (`C:\dev\claude` + scratch) |
| `config.py`'s `load_secret` reads the secret file at startup only and is not caller-influenced; `divoid_edit_content` has no `path` param | The error code and its shape |
| Reachability: MCP is blanket-allowlisted, `defaultMode:auto`, no per-call prompt | Whether a byte cap belongs in this change |
| Prompt-injection framing: graph content is untrusted when it names a path | The severity call is Toni's (4/CRITICAL), not the finder's ("Serious") |

Every re-derived item below is argued from this repo's own constraints, and three of them land somewhere different from the proposal.

---

## 2. Scope & Non-Scope

### In scope

- `divoid-mcp/src/divoid_mcp/tools/download_content.py` — the write side.
- `divoid-mcp/src/divoid_mcp/tools/set_content.py` — the read side.
- One new shared module, `divoid-mcp/src/divoid_mcp/paths.py`.
- One bootstrap line in `divoid-mcp/src/divoid_mcp/server.py`.
- Smoke coverage in `divoid-mcp/tests/smoke/` per #6104 step 4.
- Tool-description text for both tools (the model-facing contract — #6104 §1 notes this string does real work).

### Explicitly out of scope

- **The harness layer** (`~/.claude/settings.json` `PreToolUse` matcher + `workspace-boundary-guard.py`). Owned by the orchestrator; machine-wide, non-repo, peer sessions live. §14 states only the *contract* the two layers must share, not the hook implementation.
- **The guard's missing PowerShell mutating cmdlets.** Separate, owned by the orchestrator.
- **A byte/size cap on either tool.** Deliberately excluded — see §12.4. This is a divergence from #10473's stated acceptance criteria; see §16.
- **Any other MCP server on the machine.** This fixes `divoid-mcp` only. The general "MCP is a side door around the boundary guard" problem is closed by the harness layer, not by this change.
- **DiVoid backend changes.** `divoid-mcp` is a pure client wrapper (`divoid-mcp/CLAUDE.md`); nothing here touches the API, schema, or backend.
- **Every other registered tool.** Verified against the registry in `tools/__init__.py`: `download_content` and `set_content` are the only caller-influenced filesystem touchpoints in the package. (Stated as a property of the registry rather than as a count — counts in documents go stale, and this arc has already produced three that did.)

---

## 3. Assumptions & Constraints

### Constraints inherited from the repo

| # | Constraint | Source |
|---|---|---|
| C1 | Pure client wrapper — no backend/API/schema change | `divoid-mcp/CLAUDE.md` |
| C2 | The API key never leaves the process; never in logs, errors, or return values | invariant 1 |
| C3 | All logs to stderr; stdout is the JSON-RPC stream | invariant 2 |
| C4 | Guard runs before any HTTP call; violations raise `InvariantViolation` | invariant 5 |
| C5 | The system layer never enforces client **vocabulary** — if the backend accepts it, pass it through | invariant 6 |
| C6 | Real logic lives in `_execute` (the smoke-test seam); a check outside `_execute` is a check the tests do not drive | #6104 §2 |
| C7 | Python 3.11+; 100-char lines; type hints | `divoid-mcp/CLAUDE.md`, `.editorconfig` |
| C8 | The operational server is a **pinned, non-editable** git install; a fix ships only after merge + reinstall + host restart | `divoid-mcp/CLAUDE.md` |

**On C5.** A reviewer could argue path containment is "client vocabulary" the MCP must not police. It is not. Invariant 6 governs *values the backend would accept* — status strings, node types. A filesystem path is never sent to the backend; it names a **local resource the client itself touches**. The backend has no opinion on it and cannot express one. Containment is therefore a structural invariant of the client, squarely inside invariant 5's remit.

### Assumptions

| # | Assumption | Confidence | How it was established |
|---|---|---|---|
| A1 | The MCP host launches the stdio server with cwd = the session's project checkout | **Measured** | `divoid_download_content(node_id=10472, path="_sarah_cwd_probe_9f3a.txt")` landed the file at `C:\dev\claude\divoid\_sarah_cwd_probe_9f3a.txt` (probe removed afterwards) |
| A2 | The server process never calls `os.chdir` | **Verified** by inspection of the package | mitigated regardless: roots are snapshotted once at startup |
| A3 | The threat model is a *confused or steered* agent, not a concurrent local attacker racing the process | Assumed | stated so that TOCTOU can be honestly excluded rather than silently ignored (§10.4) |
| A4 | One MCP server process per host session | Assumed, consistent with A1 | if false, a single process would serve several cwds and the derived root would be wrong for all but one — see §17 Q1 |
| A5 | Deployment platform is Windows; the package must still be correct on POSIX | Given | every rule below is stated platform-neutrally with the Windows specifics called out |

---

## 4. Does the `path` parameter need to exist at all?

The brief asks this directly, because removing a primitive is strictly stronger than fencing one. I weighed it and the answer is **keep both, fence both**. The reasoning, per parameter:

**`divoid_set_content(path=…)`** was added (#8523 Defect 2, #7895 Finding 1) because the alternative — the agent re-emitting a large body inline through `content` — was **measured to corrupt the body via transcription drift**. The harness `Read` tool is not a substitute: `Read` puts the file through the model's token channel, which is the defective step. Removing `path` does not move the risk elsewhere; it reinstates a known defect.

**`divoid_download_content(path)`** exists (#6597) to land **binary** node content on disk byte-identically — images, PDFs, blobs. There is no harness substitute at all: `Write` can only write bytes the model emitted, and the model cannot emit a PDF. Removing `path` deletes the tool's entire reason to exist.

**A narrower variant I also rejected: "keep `path` but accept relative paths only."** It is syntactic, cheap, and self-evidently workspace-shaped. It fails on two counts. First it is not containment — `..\..\Windows\Temp\x` is a relative path — so it would need the containment check anyway and buys nothing on top. Second it breaks every current caller: the agent operating contract on this machine mandates absolute paths ("*please only use absolute file paths*"), because agent bash threads reset cwd between calls.

**Conclusion.** The primitive is not the vulnerability; the *unbounded* primitive is. Bound it.

---

## 5. Architectural Overview

```
                          startup (server.py bootstrap)
                          ─────────────────────────────
   DIVOID_MCP_FILE_ROOT ──┐
   (os.pathsep-separated) │
                          ├──►  paths.init()  ──►  ROOTS: frozen tuple of
   os.getcwd() (default) ──┘        │                     resolved, normalised
                                    │                     absolute directories
                       root sanity gate, PER CANDIDATE
                       (has a parent? not $HOME?)
                                    │
                     ┌──────────────┴───────────────┐
              candidate usable              candidate unusable
                     │                              │
                     ▼                              ▼
              appended to ROOTS               discarded + WARNING naming it
                     │                              │
                     └──────────────┬───────────────┘
                                    ▼
                    ROOTS = (surviving candidates…)
                                    │
                     ┌──────────────┴───────────────┐
              at least one                    NONE survived
                     │                              │
                     ▼                              ▼
              gate adjudicates               ROOTS = ()  + WARNING to stderr
                                                    │     (server still starts;
                                                    │      every tool that takes
                                                    │      no path keeps working)
                                                    │
   ───────────────────────────────────────────────────────────────────────────
                          per call (tools/*.py `_execute`)
   ───────────────────────────────────────────────────────────────────────────

   caller path ──►  ┌──────────────── PATH GATE (paths.py) ─────────────────┐
                    │ 1. ROOTS empty            → file_root_unusable        │
                    │ 2. \\?\ \\.\ //?/ //./    → path_outside_root         │
                    │ 3. embedded NUL byte      → path_outside_root         │
                    │ 4. realpath()  (any raise → path_outside_root)        │
                    │ 5. normcase + commonpath vs each root in turn;        │
                    │    ValueError → that root abstains, try the next;     │
                    │    no root matched → path_outside_root                │
                    └──────────────────┬───────────────────┬────────────────┘
                                       │ pass              │ fail
                                       ▼                   ▼
                              RESOLVED path         InvariantViolation
                                       │             → make_error_content
                    ┌──────────────────┴─────────────────┐
                    ▼                                    ▼
        download_content._execute            set_content._execute
        makedirs(dirname(RESOLVED))          open(RESOLVED, "rb")
        open(RESOLVED, "wb")                 → POST bytes
        ↑ gate runs BEFORE the GET           ↑ gate runs BEFORE the POST
```

Three design commitments are visible in that diagram and each is load-bearing:

1. **The gate runs before the HTTP call, not just before the disk touch.** Invariant 5, and it satisfies #10473's "touch no disk" criterion a fortiori: a rejected call performs no network I/O either, so node content is never even pulled into the process.
2. **The gate returns the resolved path and the tools open *that*.** This is the difference between a check and a boundary. Measured below (§9.2), resolution and the raw string are not interchangeable for accepted inputs: a drive-relative `C:foo\bar.txt` is accepted and lands at `C:\dev\claude\DiVoid\foo\bar.txt`, a target not derivable from the input string and dependent on per-drive current-directory state that the gate has already collapsed; and resolution follows a junction where the Win32 opener would collapse `..` lexically instead (§9.3). Opening the raw string re-introduces every one of these.
3. **An unusable root disables the two path tools rather than killing the server.** See §12.3.

---

## 6. Components & Responsibilities

| Component | Owns | Does **not** own |
|---|---|---|
| **`paths.py`** (new) | The frozen root list; the root sanity gate; the path gate (syntactic pre-filter → resolution → containment); raising `InvariantViolation("path_outside_root", …)` | Any I/O on the path; any HTTP; any knowledge of nodes, node ids, or content |
| **`server.py`** (1 line) | Calling `paths.init()` during bootstrap, in the same phase as `http_client.init()` | The containment rule itself |
| **`download_content.py` `_execute`** | Calling the gate first, then using the returned resolved path for `makedirs` + `open`; reporting the resolved path in its result | Deciding what is contained |
| **`set_content.py` `_execute`** | Calling the gate immediately before `open(…, "rb")`, on the resolved path | Deciding what is contained |
| **`errors.py`** | Unchanged. `InvariantViolation` + `make_error_content` already carry this shape | — |

### 6.1 Why a module and not two inline checks (DRY math, per #1136 §1)

The gate is a syntactic pre-filter, a wrapped resolution, a normalised component comparison, and error construction — approximately **25 lines × 2 sites = 50 lines** of duplication if inlined. Well above the ~15–20 threshold from DiVoid #1267, so the extraction is required, not optional. It also passes the named-helper test in 2 words. Beyond the line count there is a security-specific reason the threshold does not capture: **two copies of a security predicate drift, and the drift is silent** — the tool that keeps the older copy stays exploitable while the newer copy makes the change look shipped.

### 6.2 Why a module-level frozen root rather than a field on `DivoidConfig`

`http_client.py` already establishes exactly this pattern: `init(...)` from bootstrap, module-level state, module functions that use it. Reusing it is DRY with the codebase's own idiom and requires no plumbing into either tool's signature. `DivoidConfig` is the *secret* container (`load_secret` is its constructor, documented as such); adding filesystem policy to it would mix concerns and force a constructor change for no gain.

The frozen-at-startup part is not incidental. Reading `os.getcwd()` per call would make the boundary depend on process-global mutable state; a single `os.chdir` anywhere in the process (including inside a future dependency) would silently move the fence. Snapshotting once removes that class entirely.

---

## 7. Interactions & Data Flow

### 7.1 `divoid_download_content` — success

1. `_execute` validates `node_id >= 1` and non-empty `path` (**unchanged**).
2. `_execute` calls the path gate with the caller's `path`. It returns a resolved absolute path.
3. `_execute` performs the `GET nodes/{id}/content` (unchanged, including the 404 / `node_has_no_content` branches).
4. `_execute` creates the parent directory of the **resolved** path and writes the bytes to the **resolved** path.
5. Result: `{success, path, bytes_written, content_type}` where `path` is now the **resolved absolute path** — see §8.3.

### 7.2 `divoid_download_content` — rejection

Steps 1–2 as above; the gate raises. `_execute` returns `{"isError": True, "content": make_error_content("path_outside_root", …)}`. **No HTTP request is issued. No directory is created. No file is opened.**

### 7.3 `divoid_set_content` — success

1. The registered shim runs `_check_invariants(content, path)` (**unchanged**: `content_path_conflict` / `content_path_required` / `content_empty` / `path_empty`).
2. `_execute` — when `path is not None` — calls the path gate **first**, before `open`.
3. `_execute` reads the **resolved** path, keeps the existing `file_not_found` / `file_read_failed` / `file_empty` branches, and POSTs the bytes.

### 7.4 `divoid_set_content` — rejection

The gate raises inside `_execute`; `_execute` returns the error envelope. **No file is opened. No HTTP request is issued.**

### 7.5 Ordering rationale — why the gate is *inside* `_execute`

`set_content` already has a `_check_invariants` function, and putting the containment check there is the superficially obvious choice. **It must not go there.** `_check_invariants` is called from the registered shim; `_execute` is called *directly* by the smoke tests and is the function that performs the `open`. A check that lives in the shim is a check the tests do not drive (C6) and, more importantly, a check that the code path performing the I/O can be reached without. **A security predicate must be co-located with the operation it guards.** In this codebase that means: inside `_execute`, on the same code path as the `open`, with no branch between them.

This is a deliberate, stated departure from the letter of the #6104 idiom (guard in `_check_invariants`, thin `_execute`). The idiom's purpose — no half-validated request reaches the backend — is preserved and strengthened; only the placement changes, and the reason is that #6104 was written for *structural* invariants where bypass is a test-hygiene issue, not a vulnerability.

---

## 8. Contracts & Interfaces (Abstract)

### 8.1 `paths.py` operations

| Operation | Input | Output | Failure |
|---|---|---|---|
| **Initialise roots** | the `DIVOID_MCP_FILE_ROOT` environment value (may be absent); the process working directory | none — establishes the frozen root tuple for the process lifetime | never raises. Candidates are adjudicated **individually**: an unusable or unresolvable candidate is discarded with its own WARNING to stderr naming it, and the surviving candidates still form the root tuple. The tuple is **empty only when every candidate was discarded**, and that case logs a further WARNING naming the env var |
| **Read roots** | none | the frozen tuple of resolved absolute directories (possibly empty) | never raises |
| **Gate a path** | one caller-supplied path string | the **resolved absolute path**, guaranteed to lie at or beneath one root | raises `InvariantViolation` with code `file_root_unusable` (roots empty) or `path_outside_root` (every other rejection) |

The gate is deterministic and side-effect-free apart from the `stat` calls `realpath` performs. It creates nothing and opens nothing.

### 8.2 Error vocabulary

Exactly **two** stable codes are introduced.

| Code | Meaning | The caller's remedy |
|---|---|---|
| `path_outside_root` | the resolved path is not at or beneath any configured root; **or** it could not be resolved; **or** it used a rejected syntax — an extended-length/device prefix, or an embedded NUL byte (§9.4) | choose a path inside the workspace root, which the message names |
| `file_root_unusable` | the server has no usable root; path I/O is disabled process-wide | none available to the agent — the message must say so and name `DIVOID_MCP_FILE_ROOT` so the human operator can act |

**Why one code and not four.** Distinct codes for "traversal", "symlink escape", "extended-length prefix", and "different drive" would be vocabulary the caller cannot act on differently — every one of them has the identical remedy. A second code is justified only where the remedy genuinely differs, and `file_root_unusable` is the one place it does: the agent can change its path, but it cannot change the server's configuration. Adding codes with no distinct remedy is exactly the kind of unearned surface Design Contracts §4 asks to delete.

**The rejection message must contain, and must be phrased so an agent does not read it as a tool defect:**

1. The path **as resolved** — not merely as supplied. For inputs like `C:foo\bar.txt` (drive-relative) or a path traversing a junction, the resolved form is not derivable from the input string, and without it the agent cannot tell *why* it was rejected.
2. The active root(s), verbatim.
3. An explicit statement that this is an **intentional containment boundary**, not a bug, and that the correct response is to choose a path inside the root — **not** to retry, not to re-encode the path, not to fall back to raw REST.

Point 3 is not decoration. Every other error this package emits (`divoid_unreachable`, `divoid_server_error`, `partial_state`) describes something that *went wrong*, so an agent's prior is that an error means "try something else". Here, "try something else" is precisely the failure mode — an agent that reads `path_outside_root` as a transient tool fault will reach for `curl` or a shell redirect and route around the fence it just hit. The message has to close that door in words.

### 8.3 Behavioural changes to existing contracts

| Surface | Before | After | Assessment |
|---|---|---|---|
| `download_content` result `path` | echoes the caller's string verbatim | the **resolved absolute path** | Improvement. For a relative input the old value was ambiguous; the new value truthfully answers "where did the bytes land". |
| `download_content` parent-directory creation | any tree, anywhere | only inside a root | The documented feature is preserved exactly, now bounded. |
| Relative paths | resolved against process cwd | **unchanged** — still resolved against process cwd, and under the default root that is the root itself | No change. See §12.2. |
| Every path inside the root | works | works identically | No regression. |
| Every path outside the root | worked | `path_outside_root` | **This is the breaking change.** See §13.1. |

### 8.4 Configuration

| Name | Kind | Default | Justification against Design Contracts §3 |
|---|---|---|---|
| `DIVOID_MCP_FILE_ROOT` | environment variable, `os.pathsep`-separated list of directories; **replaces** the default entirely | unset → the process working directory | §3's carve-out "*the value differs between environments by design*" applies **literally and by name**: `divoid-mcp/CLAUDE.md` states the package is "a generic-purpose tool used outside this deployment", and the derived default is only meaningful on hosts that set a useful cwd. There is also a named operator (Toni) and a named event (installing this fix, to decide whether `C:\dev\claude\_scratch` is added). |

`os.pathsep` rather than the finding's comma: a comma is a legal character in a Windows directory name, so a comma-separated list is not parseable in general. `os.pathsep` is the platform's own list convention (`PATH` uses it). Accepted limit: a directory containing `;` (Windows) or `:` (POSIX) cannot be expressed — the same limit `PATH` has carried for thirty years.

**Root sanity gate.** A root is adopted only if it (a) has a parent — i.e. is not a filesystem or drive root — and (b) is not the user's home directory itself. Both are one comparison each and each prevents a specific catastrophic degeneration: a drive-root root makes "contained" mean "the whole volume"; a home root puts `~/.ssh` and `~/.claude/secrets/.divoid-online` — the exact file #10472 names as the exfiltration target — back inside the fence.

**I deliberately did not add a system-directory blocklist** (`C:\Windows`, `C:\Program Files`, …). A blocklist of "bad places" is unbounded and adversarially incomplete, which is the shape #1220 §5 warns about — it would read as protection while being trivially defeated. The two-rule gate bounds the *breadth* of a bad root, not its *location*, and I state that as the limit rather than papering over it (§10.7).

---

## 9. The Containment Predicate — and What Would Falsify It

This is a security boundary, so per **#1220 §5 addendum** every claim below is stated with the inputs that would break it if it were wrong, not the inputs that show it working. Each row is marked **MEASURED** (I ran it) or **REASONED** (I did not).

### 9.1 The predicate

> A caller-supplied path is acceptable iff, after (i) rejecting the extended-length and device namespaces **and any embedded NUL byte** syntactically and (ii) resolving through `os.path.realpath`, the result is **equal to, or a descendant of, one of the frozen roots**, where descent is decided by **path components after `os.path.normcase`** — never by string prefix.

**The two exception classes are handled differently, and conflating them breaks multi-root configurations.** An exception raised during **resolution** is terminal: the path is rejected immediately, because there is no resolved form to compare. An exception raised during **comparison against one root** is *not* terminal: that root simply cannot adjudicate this path, so it abstains and the next root is tried. Rejection follows only when **no** root has matched after every one has been tried. For the single-root default the two rules produce the same outcome, which is why the distinction is easy to miss — an implementation that raises on the first `ValueError` is correct for the default and refuses every path in a multi-root configuration whose *first* root happens to sit on another drive.

Net effect, which is what the boundary claim rests on: **no exception anywhere on this path can produce an "allow".**

Measurement environment: Python 3.14.2, Windows 11, process cwd `C:\dev\claude\divoid`, candidate root `C:\dev\claude\divoid`. **The table is measured against a single root**, so its "component compare" column is the verdict of *that one* root. With several roots configured, a `ValueError` in that column means the root abstains and the next is tried; only the exhaustion of all roots is a rejection (§9.1).

> **Methodology, and a correction (2026-09-01).** The first version of this table reported that `os.path.realpath` *mangles* `\\?\` paths into a drive-relative form (`C:Windows\Temp\x.txt`), and inferred from that a risk that a second resolution pass could land such a path inside the root. **That measurement was an artefact and the inference was wrong.** The probe script had been written through a shell heredoc that collapsed `\\` to `\`, so the interpreter actually received the single-backslash form `\?\C:\…`. Only inputs containing a **doubled** backslash were affected — every single-backslash row was unharmed, which is why the corruption was not obvious. Caught by John during implementation and re-measured by the coordinator.
>
> The table below was re-measured from a probe file written directly to disk (no shell layer), with each input built from `chr(92)` and each row self-verifying its own leading-backslash count before use. **Every row marked MEASURED here has been re-run under that method.** The lesson generalises past this document: when a measurement is about backslashes, the measuring apparatus must not be a shell.

### 9.2 Measured behaviour

| Input | `os.path.realpath` → | component compare | naive `startswith` | note |
|---|---|---|---|---|
| `C:\dev\claude\divoid\ok.txt` | `C:\dev\claude\DiVoid\ok.txt` | **inside** | inside | baseline |
| `C:\dev\claude\divoid\..\..\..\Windows\Temp\x.txt` | `C:\Windows\Temp\x.txt` | **outside** | outside | traversal collapsed |
| `C:\dev\claude\divoid-evil\x.txt` | `C:\dev\claude\divoid-evil\x.txt` | **outside** | **INSIDE** ← | **the string-prefix check is broken** |
| `c:\DEV\Claude\DiVoid\OK.txt` | `C:\dev\claude\DiVoid\OK.txt` | **inside** | inside | case folding required |
| `\\?\C:\Windows\Temp\x.txt` | `\\?\C:\Windows\Temp\x.txt` — **prefix preserved** | `commonpath` **raises ValueError** | outside | comparison cannot adjudicate this form at all |
| `\\?\C:\dev\claude\divoid\..\..\Windows\x` | `\\?\C:\dev\Windows\x` — prefix preserved | `commonpath` **raises ValueError** | outside | traversal collapses correctly; result is genuinely outside |
| `\\?\C:\dev\claude\divoid\a.txt` — *genuinely in-root* | `\\?\C:\dev\claude\DiVoid\a.txt` | `commonpath` **raises ValueError** | outside | **even a valid in-root extended path is unadjudicable** |
| `//?/C:/Windows/Temp/x.txt` | `\\?\C:\Windows\Temp\x.txt` | `commonpath` **raises ValueError** | outside | forward-slash form **normalises to** the backslash form |
| `\\.\C:\dev\claude\divoid\a.txt` | `C:\dev\claude\DiVoid\a.txt` — **prefix stripped** | **inside** | inside | device form **launders into an ordinary in-root path** |
| `\\.\C:\Windows\Temp\x.txt` | `C:\Windows\Temp\x.txt` — prefix stripped | outside | outside | device form stripped; lands outside |
| `\\.\GLOBALROOT\Device\HarddiskVolume3\Windows\Temp\x.txt` | — | **`realpath` itself raises `OSError` (WinError 6)** | — | resolution can raise on a device path |
| `\\server\share\x.txt` | `\\server\share\x.txt` — **preserved, not rewritten** | `commonpath` **raises ValueError** | outside | UNC comparison is undefined, not "outside" |
| `C:foo\bar.txt` | `C:\dev\claude\DiVoid\foo\bar.txt` | inside | inside | drive-relative; resolved form not derivable from the input |
| `\Windows\Temp\x.txt` | `C:\Windows\Temp\x.txt` | outside | outside | root-relative |
| `D:\elsewhere\x.txt` | `D:\elsewhere\x.txt` | `commonpath` **raises ValueError** | outside | cross-drive |
| `C:\dev\claude\divoid\deep\not\exist\yet\x.txt` | `C:\dev\claude\DiVoid\deep\not\exist\yet\x.txt` | inside | inside | non-existent tail resolves lexically — the `makedirs` case |
| `C:\dev\claude\DIVOID~1\x.txt` | `C:\dev\claude\divoid-frontend\x.txt` | **outside** | outside | 8.3 short name resolves to a **different sibling checkout** |
| `C:\dev\claude\divoid\NUL` | `C:\dev\claude\DiVoid\NUL` | inside | inside | reserved device name — **accepted; see §10.5** |
| `C:\dev\claude\divoid\a.txt:hidden` | `C:\dev\claude\DiVoid\a.txt:hidden` | inside | inside | alternate data stream — **accepted; see §10.6** |
| `~/secret.txt` | `C:\dev\claude\DiVoid\~\secret.txt` | inside | inside | **no** tilde expansion |
| `%USERPROFILE%\secret.txt` | `C:\dev\claude\DiVoid\%USERPROFILE%\secret.txt` | inside | inside | **no** environment expansion |
| `relative.txt` | `C:\dev\claude\DiVoid\relative.txt` | inside | inside | relative resolves against cwd = the root |
| `<root>\a\0.txt` (embedded NUL byte) | **never resolved** — rejected by the prefilter first | **rejected** (`path_outside_root`) | — | platform note: had it reached resolution, `realpath` would *not* raise and comparison would say "inside"; `open()` would then raise `ValueError`. That is why the prefilter exists. See §9.4 |

Four of these rows are the ones a reviewer would use to break a careless implementation, so each is called out explicitly:

- **`divoid-evil` is the falsifier for `startswith`.** `os.path.realpath(p).startswith(root)` — the shape a hurried implementation reaches for — accepts a *sibling directory whose name extends the root's name*. The requirement to compare components (`os.path.commonpath`, or `Path.relative_to` with a `..` check) is not stylistic; it is the difference between a fence and a hole.
- **The `\\?\` and `\\.\` families are the falsifier for "just realpath it" — but not for the reason the first draft claimed.** Resolution handles them *correctly* and **inconsistently with each other**: `\\?\` paths keep their prefix and resolve accurately (`\\?\C:\dev\claude\divoid\..\..\Windows\x` → `\\?\C:\dev\Windows\x`, genuinely outside), while `\\.\` paths have the prefix **stripped** and normalise into ordinary paths — so `\\.\C:\dev\claude\divoid\a.txt` becomes `C:\dev\claude\DiVoid\a.txt` and lands **inside** the root. And `\\.\GLOBALROOT\Device\…` makes `realpath` raise `OSError` outright.

  The justification for rejecting these prefixes syntactically is therefore **not** any specific mangling. It is that **resolution behaviour for them is form- and version-dependent, and the boundary must not depend on it.** Three distinct behaviours across two adjacent prefixes on one interpreter is the evidence; a different Python or a different Windows build may produce a fourth. On top of that, `commonpath` **raises `ValueError` for every `\\?\` form — including a genuinely in-root one** — so containment cannot return a verdict for this family even when the path is legitimate. A prefilter that runs *before* resolution makes the gate's behaviour for these inputs a property of our own code rather than of the platform's normalisation rules. That argument is both true and stronger than the mangling story it replaces.
- **`D:\`, UNC, and the `\\?\` cases are the falsifier for unguarded comparison.** `os.path.commonpath` raises `ValueError` across drives, and `os.path.realpath` raises `OSError` on `\\.\GLOBALROOT\Device\…`. An implementation that lets either propagate crashes the tool; one that catches it and then *proceeds to the I/O* would be worse. Catching a comparison `ValueError` and continuing **to the next root** is correct and is what ships (§9.1) — the two "continues" are opposite decisions and must not be confused. No exception path can produce an allow. **The re-measurement sharpened this**: a UNC path is *not* rejected by the comparison returning "outside" — the comparison **raises**, so for UNC the exception handler is the only thing standing between an SMB destination and the filesystem. The catch-all is load-bearing, not belt-and-braces.
- **`DIVOID~1` resolving to `divoid-frontend` is the falsifier for "the root is obvious".** There is a *second checkout* adjacent to this one, and an 8.3 short name reaches it. It resolves outside the root, so it is rejected — but the same measurement is the strongest argument against the finding's proposed default of `C:\dev\claude`, which would have put that sibling **inside** the fence.

### 9.3 Reasoned, not measured

Stated separately so a reviewer knows which claims carry evidence and which carry an argument.

- *(The embedded-NUL case was reasoned here in the first draft, then **measured** — which contradicted the reasoning — and then **fixed in code**. It now lives in §9.4 as a recorded decision, not an open question.)*
- **Trailing dots and spaces** (`x.txt.`, `x.txt `) are stripped by Win32 at open time but not by resolution, so the compared string and the opened file differ in name. This cannot move a file across a directory boundary — stripping affects only a component's name — so it is a false-*deny* risk at worst.
- **Root-is-a-junction.** If a root is itself a junction or symlink, resolving *both* the root and the candidate through the same routine makes them agree. This is why the root must be resolved at `init` time with the identical routine, not merely `abspath`-ed. Measured corroboration: `os.getcwd()` returns `C:\dev\claude\divoid` while `realpath` of it returns `C:\dev\claude\DiVoid` — the on-disk casing differs from what the host handed the process. `normcase` absorbs *that* particular difference, but it would not absorb a junction, and the implementer must not conclude from the case-only observation that resolving the root is optional.
- **Lexical `..` collapsing over a symlinked component.** `root\junction\..\x` collapses lexically to `root\x`, but resolution follows the junction first and lands elsewhere — so resolution is *stricter* than the Win32 opener here. A false deny, safe direction. It is safe **only because the tools open the resolved path**; opening the raw string would let the two interpretations diverge.

### 9.4 Embedded NUL — DECIDED: folded into this change

**Status: implemented.** In `paths.py`, the embedded-NUL check inside `gate()`'s syntactic prefilter — after the `_REJECTED_PREFIXES` loop, before the `os.path.realpath` call — rejects any path containing a NUL byte, reusing `path_outside_root` rather than minting a new code. *(Referenced by symbol deliberately: this document has already carried one line range that expired within minutes of being measured. Do not "helpfully" restore line numbers here.)* Pinned by Jenny's mutation M3 (review #10494). Verified by running the shipped gate: an in-root control is accepted, `<root>\a\0.txt` is rejected with `path_outside_root`.

**The platform behaviour that made it necessary** — measured, and the reason the first draft's *reasoning* about this case was wrong:

- `os.path.realpath` **does not raise** on an embedded NUL; it returns the string unchanged.
- Component comparison therefore reports it **inside** the root.
- `open()` then raises **`ValueError: embedded null character`** — and `ValueError` is not `OSError`, so it escaped the `except OSError` handlers at *both* `open()` sites and would have surfaced as an unhandled exception rather than a mapped error envelope.

**Why it was folded in rather than deferred.** I originally recommended deferring, on the grounds that this is a robustness defect rather than a containment one. The coordinator ruled it rides along, and that is the better call: it is **one comparison, on the code path this change already introduces, in the module this change already creates**. Deferring would have meant filing a task, re-establishing the context, and re-touching `paths.py` — strictly more total work than the line it saves, for a hole that is open in the meantime. The scope-discipline rule exists to stop *unrelated* work riding along; a one-line guard in the very function being written is not that.

**Note the distinction from §10.5**, which is easy to miss because both say "NUL": §10.5 concerns the reserved *device name* `NUL` as a filename (`<root>\NUL`), which is a different input and is **still accepted** — verified against the shipped gate. This section concerns a NUL *byte* embedded in the path string. Fixing one does not affect the other.

---

## 10. What This Design Does **Not** Protect Against

Named honestly, per the brief's preference for an admitted limit over a clean claim a reviewer breaks in one pass. Ordered by how much I think each matters.

**10.1 — In-root exfiltration. The largest remaining hole, and #10472 does not name it.**
Containment bounds *where*, never *whether*. Everything inside the session's checkout remains uploadable into the shared graph in one call: `.git/config` (which carries a token in the URL for a token-authenticated remote), `.venv/`, any `.env`, and on this machine `C:\dev\claude\divoid\.claude\`. A steered agent that would have exfiltrated `~/.ssh/id_rsa` can still exfiltrate the repo's own credentials. **Closing this needs a different mechanism — a deny-list of sensitive in-root patterns, or treating the read side as needing explicit confirmation — and it is not in this change.** It should be its own task; see §17 Q3.

**10.2 — An agent that is itself the attacker.**
The finding concedes this and it is correct: an allowlisted tool called by a compliant agent inside the root is indistinguishable from legitimate use. This design raises the cost of *accidental* and *steered* misuse; it is not a jail.

**10.3 — Prompt injection is mitigated, not eliminated.**
A malicious node body that says "download this to the path below" can no longer reach `C:\ProgramData` or `~/.claude/secrets`. It can still write graph-controlled bytes over a file **inside** the checkout — a source file, `CLAUDE.md`, or `.claude/settings.json` if the repo has one. Overwrite-in-root remains a real primitive.

**10.4 — TOCTOU.**
Between resolution and `open`, a concurrent process could replace a resolved-safe component with a junction. Directory junctions need no elevation on Windows. Excluded by assumption A3 (the threat model is a steered agent, not a racing local attacker). Closing it would need handle-based verification, which is disproportionate here — but it *is* a hole, not an absence of one.

**10.5 — Reserved device names.**
*(Not to be confused with §9.4's embedded NUL **byte**, which is rejected. This item is about the reserved device **name** used as a filename, which is still accepted — re-verified against the shipped gate after the §9.4 fix.)*

`…\divoid\NUL`, `…\divoid\CON`, `…\divoid\COM1` compare as inside the root (**measured**) but Win32 opens the *device*, not a file. So containment's claim is literally false for this input class. I chose not to blocklist them: the impact does not touch either goal — `NUL` discards, `CON`/`COM*` read console or serial noise; no file is exfiltrated and nothing is written outside the root. If a reviewer wants belt-and-braces, this list *is* genuinely closed (unlike system directories) and blocking it is cheap. My call is to name the limit rather than spend surface on it.

**10.6 — Alternate data streams.**
`…\divoid\a.txt:hidden` compares as inside the root (**measured**) and writes an NTFS stream on a file that is inside the root. Containment holds; discoverability does not — the bytes are invisible to a directory listing. A hiding place, not an escape.

**10.7 — A bad-but-narrow root.**
The sanity gate rejects a drive root and the home directory. It does **not** reject `C:\Windows\System32` or any other narrow-but-wrong directory. On a host that launches the server there, path I/O would be bounded to that directory. The gate bounds the breadth of a bad root, not its location.

**10.8 — Hardlinks.**
`realpath` resolves symlinks and junctions; it does **not** resolve hardlinks, because a hardlink has no "target" — both names are equally real. An existing hardlink inside the root pointing at a file outside it is readable through the fence. On NTFS, `mklink /H` needs no elevation. Mitigating this would require comparing volume + file index, which is disproportionate. Mitigating factor, not a defence: creating the hardlink requires write access inside the root already.

**10.9 — NTFS per-directory case sensitivity.**
`normcase` folds case unconditionally. On a directory flagged case-sensitive, two genuinely distinct paths fold together. For this to become a false *accept*, a real out-of-root path would have to fold onto an in-root one, which requires a case-variant sibling of the root itself. Narrow, but not zero.

**10.10 — The override is itself a weakening primitive.**
Anything able to edit `.mcp.json` or the host configuration can set `DIVOID_MCP_FILE_ROOT` to `C:\`. Same class as "the agent can edit the hook", and unavoidable for any process-configured boundary.

**10.11 — Scope.**
Every other MCP server on the machine with a path-bearing tool remains unguarded. Only the harness layer (§14, out of scope) closes that class.

---

## 11. Cross-Cutting Concerns

**Security.** Fail-closed at every branch: no roots → reject; resolution raises → reject; comparison against a root raises → that root abstains, and if none matches → reject; unknown path syntax → reject. There is no code path where an unhandled condition yields "allow".

**Secrets (C2).** The gate handles paths and roots only, never the API key. But `path_outside_root` messages **echo a caller-supplied string**, and a caller could pass a path whose *name* contains a secret. Existing practice already covers this shape: `set_content`'s `file_not_found` echoes `path!r` today. The design does not change it, and the messages must continue to route through `make_error_content` rather than being hand-formatted (#6104 §5).

**Observability (C3).** Both tools already log the path at INFO. Add a WARNING at startup for each discarded root candidate, and a further one if none survives (§8.1). Log rejections at INFO with the code and the **caller-supplied** path. Everything to stderr. Rejections must be greppable: after rollout, `path_outside_root` in the MCP stderr log is the signal for a legitimate location that the configured roots do not cover (§13.1).

**Known weakness in that procedure, stated rather than glossed.** The log line carries the path *as supplied*. For the rejections that reached resolution, the **resolved** path exists and appears in the returned error message — which is not captured in the stderr log. For the ordinary in-workspace rejection the two forms are close enough that the grep procedure works. For the inputs where they differ most — a drive-relative `C:foo\bar.txt`, a path traversing a junction, an 8.3 short name — the log alone does not say where the path actually pointed, which is exactly the information an operator needs to decide whether the rejected location was legitimate.

**Logging both values would be better, and it is not free.** By the time the tool catches the violation the resolved path exists only inside the exception's message text, so making it loggable means either logging that message verbatim or giving `InvariantViolation` somewhere to carry the resolved path — a small but real decision, not a format-string edit. Recorded here as a gap rather than written into the spec as if it shipped.

**Error handling.** No new envelope. `InvariantViolation` → `make_error_content(code, message)` → `{"isError": True, "content": …}`, exactly as every other tool.

**Idempotency / concurrency / caching.** Unaffected. The gate is pure apart from `stat`; roots are immutable for the process lifetime; there is no shared mutable state and therefore no new concurrency surface. No retries are added (invariant 3) — a rejection is terminal by construction.

**Performance.** One `realpath` per path-bearing call, on a call that is already doing network *and* disk I/O. Immeasurable.

---

## 12. Quality Attributes & Trade-offs — Alternatives Rejected by Name

### 12.1 Root source — **cwd-derived** over env-configured (the finding's proposal)

| Option | Verdict |
|---|---|
| **`DIVOID_MCP_FILE_ROOT` required, default `C:\dev\claude` + scratch** (the finding's remedy) | **Rejected.** (a) A machine-specific absolute path hardcoded into a package `divoid-mcp/CLAUDE.md` says is "used outside this deployment" is unshippable. (b) It is **measurably too broad**: §9.2 shows `C:\dev\claude\divoid-frontend` is a sibling checkout, so this root would let a session reach into another session's working tree — the exact cross-session collision hazard #8349 and #9796 were written about. (c) It requires configuration everywhere before it protects anything. |
| **`DIVOID_MCP_FILE_ROOT` required, no default** | **Rejected.** Fails safe but fails *dead*: every existing caller breaks until a human edits host config, on a fix that is meant to ship without an outage. |
| **Derived from `$HOME`** | **Rejected.** Would place `~/.ssh` and `~/.claude/secrets/.divoid-online` — the file #10472 names as the target — inside the fence. Strictly worse than the status quo's optics. |
| **Derived from the git repository root** | **Rejected.** Adds a discovery dependency and can walk *upward*, making the root broader than cwd for a session started in a subdirectory. cwd is simpler and tighter. |
| **Derived from the process working directory, overridable** | **Chosen.** Measured (A1) to be the session's checkout. Zero configuration. Scopes *per session*, so **the default is** strictly tighter than any machine-wide root and a sibling checkout is out of reach — a property of the default only; a configured list is exactly as tight as the operator makes it (§14 C-2). Matches the universal CLI convention. The override exists for hosts where cwd is not meaningful — a named environment difference, which is §3's own carve-out. |

**Named downside of the chosen option, per §4's trade-off procedure:** the root becomes narrower than the harness allowlist, so `C:\dev\claude\_scratch\…` and the harness scratchpad fall **outside** it. Probability of encountering this: moderate — those are documented agent scratch locations. Cost when it happens: one `path_outside_root` error and one env-var edit by the operator. Cost of the alternative (widening the default to `C:\dev\claude`): permanent cross-session reach for every session on the machine, forever. The narrow default wins clearly. Recommended rollout step in §15.

### 12.2 Relative paths — **allowed**, rule not added

I considered requiring drive-qualified absolute paths, on the argument that it would let the harness adjudicate the identical string. Rejected, on measurement: under a cwd-derived root, `relative.txt` resolves to `C:\dev\claude\DiVoid\relative.txt` — **inside the root by construction**. The rule would add surface and forbid the natural `download to ./out.png` ergonomic while buying zero containment. KISS: the rule that changes no outcome does not ship.

One implementation note this decision makes load-bearing: **the absoluteness test must not be `os.path.isabs`.** On Python 3.11/3.12 `ntpath.isabs("\Windows\Temp\x")` returns **True**; on 3.13+ it returns **False** (measured on 3.14.2). Any logic branching on `isabs` would behave differently across supported interpreters. Nothing in this design branches on it, and nothing added later should.

### 12.3 No usable root — **disable the two path tools**, do not exit

`config.py` sets the precedent of `sys.exit(1)` on bad configuration, so exiting is the consistent-looking choice. Rejected: it would take down every registered tool over a problem that affects only the two taking a `path`, and DiVoid access is the server's core value while path I/O is peripheral. The server starts, logs the WARNINGs from §8.1, and the two path tools return `file_root_unusable`. Accepted downside: an operator may not notice — mitigated by the startup WARNINGs and by the error message naming the env var.

Note the trigger is **no usable root at all**, not "a root candidate was bad": one discarded entry in a multi-entry list leaves the surviving entries in force and the tools working (§8.1).

### 12.4 Byte cap — **excluded**, and this is deliberate

The brief asks for an explicit ruling. **A size cap does not belong in this change, or arguably in this package at all.**

- It is a **different problem**: containment answers *where*, a cap answers *how much*. Bundling them makes one PR that reviewers must evaluate against two unrelated threat models.
- On the **write** side the bytes come from DiVoid, not from the caller — the cap would defend against "a large node fills the disk", which an agent can already do with `Write`, and which requires someone to have uploaded the large node first.
- On the **read** side it is a **client-side guess at a server-side policy**. The backend owns the storage and the quota. If the two disagree, either the client blocks an upload the backend would have accepted — which is the shape invariant 6 exists to forbid — or the cap is below the backend's limit and does nothing.
- It is a **magic number with no named operator and no named tuning event**, which Design Contracts §3 rejects outright.

If the resource concern is real, it should be a DiVoid **backend** task bounding `POST /api/nodes/{id}/content`, filed separately. See §17 Q2.

### 12.5 Attribute summary

| Attribute | Effect |
|---|---|
| Security | The stated goal is met for out-of-root read and write; §10 enumerates what remains |
| Maintainability | One module, one predicate, two call sites, two error codes. No new abstraction, no interface, no config object |
| Performance | One `realpath` per path-bearing call. Negligible against the network + disk I/O already on that path |
| Compatibility | No change for in-root callers; hard failure for out-of-root callers, which is the point |
| Testability | The gate is pure and table-driven; §9.2 doubles as the test vector table |

---

## 13. Risks & Mitigations

| # | Risk | Likelihood | Mitigation |
|---|---|---|---|
| 13.1 | A legitimate existing workflow uses an out-of-root path (`_scratch`, the harness scratchpad) and breaks on upgrade | Moderate | Rejections log at INFO with the caller-supplied path; grep `path_outside_root` after rollout, and read the returned message for the resolved form (§11 records why logging both would be better). Remedy is an env-var edit, **never** widening the default |
| 13.2 | Implementer uses `startswith` instead of component comparison | **High — this is the most likely way the fix ships broken** | §9.2 contains the exact falsifying input (`C:\dev\claude\divoid-evil\x.txt`). It must appear as a **required smoke assertion**, not a note |
| 13.3 | Implementer validates `path` but opens the raw string | Moderate | §5 commitment 2 and §7 steps make the resolved path the only thing passed to `makedirs`/`open`. A smoke case with a drive-relative input (`C:foo\bar.txt`) catches divergence |
| 13.4 | The check is placed in `set_content._check_invariants` | Moderate — it is the idiomatic-looking spot | §7.5 states the rule and its reason. Smoke tests call `_execute` directly, so a check in the shim will **fail** the required negative test |
| 13.5 | `commonpath`'s `ValueError`, or `realpath`'s `OSError`, propagates as an unhandled exception | Moderate | Measured in §9.2: `ValueError` on `D:\`, on all three `\\?\` forms, and on **UNC**; `OSError` on `\\.\GLOBALROOT\Device\…`. No exception can produce an allow: resolution failures reject outright, comparison failures make that root abstain and reject once none has matched (§9.1) — and for UNC the caught `ValueError` is the *only* thing producing a rejection |
| 13.6 | Operator "fixes" a rejection by adding `~/.claude` to the root list, restoring the exfil target | Low but severe | The `path_outside_root` message must **not** suggest widening the root to the agent, and the doc/README note for the env var must carry an explicit warning against `~`, `~/.claude`, and drive roots |
| 13.7 | Fix is merged but the operational MCP is not reinstalled, so nothing changes in practice | Moderate | C8: the operational server is a pinned git install. Rollout requires version bump → merge → `pip install --force-reinstall` → **full host restart**. State this in the PR body |
| 13.8 | An agent reads `path_outside_root` as a tool defect and routes around it via `curl`/shell | Moderate | §8.2 point 3 — the message states it is an intentional boundary and that retrying or falling back is the wrong response |

---

## 14. Contract for the Harness Layer (out of scope, stated so the layers cannot disagree)

The orchestrator owns the `PreToolUse` matcher and `workspace-boundary-guard.py`. This section states only the **contract** both layers must satisfy, not the hook's implementation.

**C-1 — Same question, same semantics.** Both layers answer *"does this path, after OS resolution, lie inside a directory the caller may touch?"* using the same resolution semantics: resolve symlinks/junctions, compare **path components** after case folding, and let no exception produce an allow — a resolution failure rejects outright, a comparison failure against one permitted directory means that directory abstains and the next is tried, with rejection only when none has matched (§9.1). A lexical or `startswith` check in the hook would disagree with the server on `C:\dev\claude\divoid-evil\…` — measured (§9.2) — producing the worst outcome available: two layers that each believe they are enforcing the same rule while enforcing different ones.

**C-2 — Different roots are expected; only one direction of divergence is permitted.** The harness enforces a machine-wide boundary (`C:\dev\claude`, `~/.claude`, `/tmp`); the server enforces whatever its frozen root list says — which **under the default** is the session's cwd, and is therefore the strictly tighter gate. **That relation is a property of the default, not of the design.** `DIVOID_MCP_FILE_ROOT` replaces the default entirely, so an operator can configure a root the harness allowlist does not contain, and in that region the server is the *looser* gate. Whoever sets that variable owns the check that every entry lies inside the harness allowlist; nothing in the server enforces it, and this document should not be read as promising that it does. **Permitted:** the harness allows a path the server rejects. **Forbidden:** the harness allows a path that resolves outside its own declared allowlist. Neither layer may treat the other's verdict as authority — each enforces independently, which is what makes this defence in depth rather than a single fence with two names.

**C-3 — Argument extraction.** For `mcp__divoid__divoid_download_content` and `mcp__divoid__divoid_set_content`, the adjudicated value is `tool_input["path"]`. For `set_content` the parameter is **optional** — absent when `content` is used inline. An absent `path` is not a violation and must not be treated as one; only a present `path` is adjudicated.

**C-4 — Relative paths.** The server resolves a relative path against its own working directory. The hook runs in a different process and cannot *prove* its working directory matches. If the hook can establish that it does, it may resolve identically. **If it cannot, it must deny rather than guess** — a guessed resolution that lands "inside" is a false accept, which is the one failure this layer exists to prevent.

**C-5 — The layers report differently.** A server rejection returns `path_outside_root` inside a normal tool result, which the agent sees and can act on. A hook denial blocks the call before the tool runs, with a different message shape. Both messages must make clear that the path was refused **by policy** — see §8.2 point 3; an agent that reads either as a malfunction will route around the layer it just hit.

**C-6 — Neither layer's presence excuses the other.** The server fix must be complete on its own: the hook is a deterrent that an agent with configuration access can disable, and it does not exist at all for non-Claude-Code MCP hosts.

---

## 15. Migration / Rollout

1. **Merge the server fix.** No data migration, no schema change, no deprecation window (private repo, atomic deploy — Design Contracts §5).
2. **Decide the operator root list for this machine before reinstalling.** Default is cwd alone. If `C:\dev\claude\_scratch` is wanted, set `DIVOID_MCP_FILE_ROOT` to the checkout plus that directory — **explicitly, not by widening to `C:\dev\claude`**, and never including `~` or `~/.claude` (risk 13.6).
3. **Bump `__version__`** (0.8.0 → 0.9.0; behaviour-changing).
4. **Reinstall the pinned operational MCP** per `divoid-mcp/CLAUDE.md` and **fully restart the MCP host**. Until this happens the fix is inert (risk 13.7).
5. **Watch stderr for `path_outside_root`** during the first days. A rejection naming a legitimate location means the root list needs one entry — not that the default needs widening.

---

## 16. Divergence from Task #10473's Acceptance Criteria

The brief invites this section explicitly, and the acceptance criteria in #10473 were written by the finder rather than by us. Three of the four survive; one does not.

| #10473 criterion | Status |
|---|---|
| "`download_content` / `set_content` to a path outside the configured root return a typed error and touch no disk" | **Met, and exceeded** — the gate runs before the HTTP call, so a rejected call touches no network either |
| "An MCP path-bearing call to an off-workspace path is denied by the guard (independently of the server fix)" | **Out of scope here**, owned by the orchestrator. §14 states the contract it must meet |
| "In-workspace paths still work; existing MCP smoke tests pass" | **Met, with one narrowing that must be recorded**: "workspace" means the **session's checkout**, not `C:\dev\claude`. Paths in a sibling checkout or in `_scratch` now require an explicit root entry. This is intentional (§12.1) |
| "Add a byte cap … to bound resource abuse" | **Not met — deliberately rejected.** §12.4. I recommend this criterion be struck from #10473 and refiled as a DiVoid **backend** task |

I also correct one detail of the proposed mechanism that would have shipped a defect: a **comma-separated** root list is not parseable, because a comma is a legal Windows directory-name character. The design uses `os.pathsep`.

**One addition beyond the brief, recorded for scope honesty:** the embedded-NUL guard (§9.4) was not in #10473's criteria and was not in the original design. It surfaced from the §9.2 re-measurement and was folded in on the coordinator's ruling. It is one comparison in the function this change already creates, closing a measured unhandled-`ValueError` path — not an unrelated feature riding along.

---

## 17. Open Questions

**Q1 — One MCP server process per session, or one shared?** Assumption A4. The cwd-derived root is correct if each host session spawns its own server (which A1's measurement is consistent with). If a single process were ever shared across sessions, its cwd would be right for at most one of them and the override would become mandatory. **For the orchestrator to confirm before John starts** — it does not change the design's shape, but it changes whether step 15.2 is optional or required.

**Q2 — Does the resource concern behind the byte cap actually exist?** If uncapped `POST /api/nodes/{id}/content` is a real problem, it is a backend task (§12.4). Someone should decide whether to file it; it should not ride along here.

**Q3 — In-root exfiltration (§10.1) is unaddressed and is now the largest remaining hole.** `.git/config`, `.env`, and `.claude/` inside the checkout stay readable into a shared graph. Options range from a sensitive-name deny-list to requiring confirmation on the read side. **My recommendation: file it as a follow-up task now**, while the reasoning is fresh, rather than letting this PR close the topic. It genuinely does not belong in this change — different mechanism, different trade-offs — but it should not disappear.

**Q4 — Should the harness scratchpad be a root?** Recommendation: **no** by default (§12.1 downside). Toni's call.

**Q5 — Documentation drift, unrelated but adjacent.** `divoid-mcp/CLAUDE.md` states a tool count and lists the tools, and both had drifted from the registry in `tools/__init__.py` — the list omitted `download_content`, `patch_link`, `edit_content`, and `delete_node`. **The durable fix is to stop stating a count at all** (#1176): a document should name the registry as the source rather than mirror a number out of it. Worth one line in the same PR since the implementer is in that file's neighbourhood, or a separate trivial task.

---

## 18. Implementation Guidance for the Next Agent

Ordered. No code appears below by design — each item is an architectural unit.

**Milestone 1 — `paths.py`.** Create the module with three responsibilities from §8.1: initialise-roots (env or cwd, each candidate resolved with the same routine used for candidate paths, each subjected **individually** to the two-rule sanity gate, survivors frozen), read-roots, and gate-a-path. The gate implements §9.1 in order: empty-roots check → syntactic pre-filter for `\\?\` `\\.\` `//?/` `//./` → **embedded-NUL check (§9.4)** → resolution wrapped so **any** exception is a rejection → `normcase` + component comparison against **each root in turn**, where a `ValueError` from the comparison means *that root abstains and the loop continues*, and rejection follows only when no root has matched. Return the resolved path on success; raise `InvariantViolation` with `path_outside_root` or `file_root_unusable` otherwise. Do not use `startswith`. Do not branch on `os.path.isabs`.

**Milestone 2 — bootstrap.** One call in `server.py`'s startup, in the same phase as `http_client.init(...)`. On an unusable root, log one WARNING to stderr naming the rejected root and `DIVOID_MCP_FILE_ROOT`; **do not exit** (§12.3).

**Milestone 3 — `download_content._execute`.** Insert the gate immediately after the existing `node_id` / empty-`path` checks and **before** the `GET`. Replace the `makedirs` and `open` targets with the resolved path. Return the resolved path in the result's `path` field (§8.3).

**Milestone 4 — `set_content._execute`.** Insert the gate at the top of the `path is not None` branch, before `open`. Open the resolved path. Leave `_check_invariants` **unchanged** — re-read §7.5 before deciding otherwise; this placement is deliberate and testable.

**Milestone 5 — smoke coverage** (`tests/smoke/`, per #6104 step 4; not pytest — print PASS/FAIL, exit non-zero). Import the real `_execute` functions, never a re-implementation. Required cases, each traceable to a row of §9.2:

| Case | Expectation | Guards against |
|---|---|---|
| In-root absolute path | success, byte-identical round trip | regression |
| Relative path | success, lands in root | §12.2 |
| `<root>-evil\x.txt` sibling | **rejected** | risk 13.2 — the single most important assertion in the suite |
| `..\..\..\Windows\Temp\x.txt` traversal | rejected | the PoC's write case |
| `\\?\C:\Windows\Temp\x.txt`, `//?/C:/Windows/Temp/x.txt`, `\\.\C:\…`, and a **genuinely in-root** `\\?\C:\dev\claude\divoid\a.txt` | all rejected, no exception escapes. Best asserted by **spying on `os.path.realpath` and requiring it is never called** for these inputs — that is platform-independent and pins the prefilter's *placement*, not merely its verdict | risk 13.5; §9.2 |
| A path on another drive | rejected via caught `ValueError` | risk 13.5 |
| A UNC path `\\server\share\x.txt` | rejected — **via the caught `ValueError`, not via an "outside" verdict** (§9.2). Without the catch-all this is an SMB exfil destination | risk 13.5 |
| **Two roots, the first on another drive**, and an in-root path under the **second** | **accepted.** This is the only case that distinguishes "a comparison `ValueError` abstains" from "a comparison `ValueError` rejects"; under the single-root default both readings pass, so without this row the multi-root semantics are untested | §9.1 |
| A path containing an embedded NUL byte | rejected in the prefilter, **before** `realpath` | §9.4. Asserting the *code* (`path_outside_root`) is not enough — the check must be shown to run before resolution, since resolution accepts this input |
| The reserved device **name** `<root>\NUL` | **accepted** — deliberately, per §10.5 | guards against a later "fix" conflating it with §9.4's NUL byte and silently narrowing the tool |
| Mixed-case in-root path | accepted | case folding |
| Drive-relative `C:foo\bar.txt` | accepted **and lands where §9.2 says** | risk 13.3 |
| `set_content._execute` called **directly** with an out-of-root path | rejected | risk 13.4 — fails if the check sits in the shim |
| Rejected call | node content **not fetched**, no directory created, no file written | #10473's "touch no disk" |

**Milestone 6 — tool descriptions.** Update both `_TOOL_DESCRIPTION` strings: paths must resolve inside the server's workspace root; out-of-root paths return `path_outside_root`; this is policy, not a fault. #6104 §1 notes this string is the model-facing contract and does real work — an agent that never learns the rule will keep proposing out-of-root paths.

**Milestone 7 — version + docs.** Bump `__version__` to 0.9.0. Add the `DIVOID_MCP_FILE_ROOT` note to `divoid-mcp/README.md` **with the warning from risk 13.6** (never `~`, `~/.claude`, or a drive root). Optionally fix the tool-count drift from Q5.

**Do not add** in this PR: a byte cap (§12.4), a system-directory blocklist (§8.4), a reserved-device-name blocklist (§10.5), retries (invariant 3), or any per-call `os.getcwd()` read (§6.2).

---

## 19. Design Contracts §5 Pre-Design Checklist

**KISS / DRY / YAGNI**
- [x] No new type mirroring an existing one — no new types at all; `InvariantViolation` is reused.
- [x] No new abstraction with one implementation — `paths.py` is a module of functions, not an interface.
- [x] No element justified by "we might need X later". The one config knob has a named environment difference **and** a named operator and event (§8.4).
- [x] No deprecation period, feature flag, compatibility shim, or transition window.
- [x] DRY math quoted: **~25 lines × 2 sites = ~50**, above the ~15–20 threshold (#1267) → extraction, with the security-specific drift argument on top (§6.1).
- [x] **Code Contracts #114 §0 YAGNI — "no defensive checks for failures that can't happen" — considered and not violated.** The broad exception handling in the gate looks like the anti-pattern and is not: §9.2 **measures** `os.path.commonpath` raising `ValueError` on five real inputs (`D:\…`, all three `\\?\` forms including a genuinely in-root one, and a UNC path), and `os.path.realpath` itself raising `OSError` on `\\.\GLOBALROOT\Device\…`. These are failures that *do* happen, on inputs a caller can supply. Per #114 §0's bounce rule the override is grounded in measurement, not paraphrase. Everything genuinely impossible was left out — there is no defensive nil-guarding of values that cannot be absent, no retry, no fallback, and no compatibility shim anywhere in this design. (The embedded-NUL check in §9.4 is not an exception to this: it guards a **measured** `ValueError` from `open()`, not a hypothetical one.)

**Existing systems first**
- [x] Audited: `errors.py` already provides the error type and envelope — reused unchanged. `http_client.py` already provides the `init`-plus-module-state pattern — mirrored rather than invented (§6.2).
- [x] The one new module's reason is named concretely: two call sites × 50 lines, plus silent drift of a duplicated security predicate. Not "cleanliness".
- [x] No new persisted data.
- [x] No transitive-dead-code chain: the gate has two live callers, both on the critical path.

**Configurability**
- [x] `DIVOID_MCP_FILE_ROOT` has a named environment difference (hosts that do not set a meaningful cwd; the package ships outside this deployment) plus a named operator and event.
- [x] No "telemetry-then-tune" compound; no audit data added.
- [x] No magic numbers introduced — the byte cap that would have been one is explicitly rejected (§12.4).

**Less is better**
- [x] Delete/merge/inline check run on every element. Deleted along the way: a system-directory blocklist, a reserved-device blocklist, a required-absolute-path rule, three of four candidate error codes, and the byte cap.
- [x] Trade-offs named explicitly where the tighter design costs something: §12.1's narrow default vs. broken scratch paths, with probability and cost on both sides.
- [x] Radical-clean where no consumer dictates otherwise: one predicate, one module, two codes.
- [x] Reader/scope inventory explicit: the two call sites are enumerated with file and line, and independently verified as the package's only caller-influenced filesystem touchpoints.

**Document discipline**
- [x] Code Contracts (#114) and Design Contracts (#1136) cited as load-bearing.
- [x] Out-of-scope items listed explicitly (§2), not merely absent.
- [x] No multi-paragraph rationale for things that obviously stay.
- [x] Supersedes no prior design; no predecessor banner needed.
- [x] Every containment claim carries its falsifier (§9), and the limits are named rather than argued away (§10).

**Data deliverables** — not applicable; no SQL, migration, or backfill.
