# divoid-mcp — harness-agnostic credential contract

> Canonical copy: this file. The DiVoid node (#13108) carries the same document verbatim, not an abstract of it. An edit to one is not finished until the other matches it byte-for-byte.
> Design target base: `7700ebb` (`main`). Every `file:line` citation below resolves against that ref.
>
> **Amended 2026-09-07 after QA #13119, and again after QA #13145.** Falsified claims are corrected in place under dated `CORRECTION` notes retaining the superseded text (#11228 Lesson 3; append-*and*-patch, #114 Addendum 2026-08-29).
> **Round 1** — behaviour change: §5.1/§5.2, the selector predicate (see §7.2 E2). Also corrected: §8.2, §9, §14. New: §12 command (B), §16.
> **Round 2** — **no behaviour change; the implementation was right and this document was not.** §7.2/§14/§15 prescribed an F4/F5 mechanism that never shipped and that defeats its own stated goal; §12's command (B) claimed a decidability it does not have; §12 carried no entry for §5.2's own user-facing consequences; §13 named a guard that does not exist and omitted eight that do.
> **The through-line, if you read only one correction:** every round-2 finding is this document *prescribing or asserting a mechanism it had not executed*. §7.2's second correction block carries the diagnosis.

## TL;DR

**What.** divoid-mcp stops treating `~/.claude/secrets/.divoid-online` as its only credential source. It reads `DIVOID_MCP_URL` + `DIVOID_MCP_API_KEY` from the process environment — the MCP specification's own answer for stdio servers — and consults the existing file only when **neither** variable is set.

**How.** `config.load_secret()` gains an environment branch, selected on **either** `DIVOID_MCP_URL` **or** `DIVOID_MCP_API_KEY` being non-empty; the branch then requires both. **A source is used whole, in both directions**: the environment never merges with the file, so a key from one instance can never be paired with a URL from another — *and* a URL set in the environment is never silently discarded in favour of the file's. Every fail-closed message names both variables and a copy-pasteable `claude mcp add … -e …` line, so a foreign environment learns the contract by *running* the server, not by reading its source. When the fallback file is used, startup logs a `WARNING` and the server's MCP `instructions` string carries one deprecation sentence — that sentence is also the Phase-2 go/no-go signal.

**Cost.** Zero migration. Existing installs (`env: {}` in the host config + the file on disk) keep working byte-for-byte. Phase 2 — prose only in this document — deletes the fallback branch once the roster has moved.

**Rejected.** Shipping a registry `server.json` as the declaration artifact: nothing consumes it, its `environmentVariables` block can only live inside a `packages` entry whose `registryType` must be one of `npm|pypi|oci|nuget|mcpb` (divoid-mcp is on none of them), and a `pip install`-from-git never receives the repo root anyway.

---

## 1. The ask

Toni, verbatim, 2026-09-07 (DiVoid task #13105):

> "the mcp currently has a burnt in token minter to a file at a hardcoded location. What is the actual standard solution for auth in mcp? - i see that in your mcp list there are some entries which state "Authentication necessary", so there is some kind of mcp way to do that. Main point is, i want it to be more flexible - if another environment loads the mcp it should not magically have to know that it needs something in a ~/.claude/secrets folder - it should either be able to provide it itself or at least some harness agnostic path which it knows has to create by looking at the mcp (not code)."

Toni, same day, deciding the resolution order and the migration shape:

> "yeah, it probably is a multi step migration - provide the stdio branch, sounds perfect, if there is no secret present there use the legacy path as fallback. As soon as the other users (currently its just our team, so not a lot) migrated we remove the fallback and have a clean auth path."

The second quote settles two things this design does not re-open: the environment is the **primary** source and the file is a **fallback, never a peer**; and the change is a **two-step migration** whose second step removes the fallback entirely.

## 2. Measured current state

| Fact | Location (at `7700ebb`) |
|---|---|
| The credential path is a module constant, `Path.home() / ".claude" / "secrets" / ".divoid-online"` | `divoid-mcp/src/divoid_mcp/config.py:22` |
| `load_secret(path=…)` accepts a path override, but **no production caller passes one** | `config.py:31`; callers: `server.py:51`, `tests/smoke/run_all.py:82` — both `load_secret()` |
| There is **no environment consultation at all** in the credential path | `config.py` imports `os` at `:15` and never uses it |
| The 401 hint hardcodes the same path in user-facing text | `errors.py:67` |
| The MCP registration on this machine carries an **empty** `env` block | `~/.claude.json` → `mcpServers.divoid` = `{"type":"stdio","command":"python","args":["-m","divoid_mcp"],"env":{}}`; `claude mcp get divoid` prints `Environment:` with nothing under it |
| The repo **already uses** the environment for its other two startup couplings | `paths.py:14` (`DIVOID_MCP_FILE_ROOT`), `server.py:36` (`DIVOID_MCP_LOG_LEVEL`), documented at `README.md:61` and `README.md:63` |
| `config.py` has **no unit test** | `tests/unit/` holds 16 test modules; none is `test_config.py` |
| The server spawns no child process, so the environment is not inherited by anything | `grep -rn "subprocess\|os.system\|popen\|Popen\|os.exec" divoid-mcp/src/` → no matches |

Auth is the **outlier**, not the pattern: two of this server's three startup couplings are already environment-configured and README-documented; the credential is the one that is not.

## 3. The standard, and what it does and does not cover

MCP specification 2025-06-18, *Authorization* § Protocol Requirements:

> - Implementations using an HTTP-based transport **SHOULD** conform to this specification.
> - Implementations using an STDIO transport **SHOULD NOT** follow this specification, and instead retrieve credentials from the environment.
> - Implementations using alternative transports **MUST** follow established security best practices for their protocol.

The "Authentication necessary" entries Toni saw in his MCP list are the **HTTP branch** — OAuth 2.1 + PKCE, RFC 9728 / 8414 / 7591. Confirmed locally: `claude mcp add --help` at `7700ebb` exposes `--client-id`, `--client-secret` and `--callback-port` and labels them *"for HTTP/SSE servers"*. divoid-mcp is stdio (`server.py:89`, `run_stdio_async`), so the spec's answer for it is *credentials from the environment*, which in an MCP client means the `env` block of the server entry — reachable from Claude Code as `claude mcp add … -e KEY=value` (verified: `claude mcp add --help` lists `-e, --env <env...>`).

So there is nothing to "conform to" beyond reading two environment variables. The work in this task is entirely in the other three quarters: **which** variables, **how they are declared**, **what the refusal says**, and **how the old path is retired**.

## 4. Scope

**In scope**

- The credential resolution order inside `config.py`, and the `DivoidConfig` it produces.
- Every user-facing string that names a credential source: the fail-closed messages, the startup INFO line, the 401 hint at `errors.py:67`, the MCP `instructions` string.
- The documentation sites that teach the contract: `README.md`, `docs/install.md`, `examples/.mcp.json`, `divoid-mcp/CLAUDE.md`, `divoid-mcp/docs/architecture/phase-1.md`.
- A unit-test module for `config.py`, which does not exist today.
- Naming Phase 2 (fallback removal) in prose, with the observable that opens it.

**Out of scope**

- Converting divoid-mcp to an HTTP transport, and therefore the entire OAuth branch. See §11.
- Any change to the DiVoid backend's API-key authentication, key issuance, or rotation.
- **Deleting `~/.claude/secrets/.divoid-online`.** The file is read by several documented `awk`-based shell recipes that are not divoid-mcp — `divoid-mcp/docs/drift-policy.md:35-36`, `divoid-mcp/docs/architecture/phase-1.md:10-11`, `divoid-mcp/tests/smoke/README.md:89-90`, and the raw-REST fallback recipe in the machine-global `CLAUDE.md`. Phase 2 removes *divoid-mcp's read of the file*, not the file. Anyone reading "clean auth path" as "the file goes away" would break those recipes.
- Validating the shape of the base URL (e.g. warning when the `/api` suffix is missing). Today's semantics are `base_url.rstrip("/")` in `http_client.py:53` and nothing else; the missing-`/api` symptom is already documented at `docs/install.md:159-161`. Adding a check is new behaviour nobody asked for.
- Retries, caching, the drift canary, and the filesystem-containment gate (`paths.py`) — untouched.

## 5. Decision 1 — where credentials come from

### 5.1 The two sources

| # | Source | Selected when | Values |
|---|---|---|---|
| 1 | **Environment** (primary) | **either** `DIVOID_MCP_URL` **or** `DIVOID_MCP_API_KEY` is present and non-empty after stripping | `DIVOID_MCP_URL`, `DIVOID_MCP_API_KEY` — **both** are required once this branch is selected; a missing one fails closed (§7.2 E1/E2) |
| 2 | **Fallback file** (deprecated) | otherwise — i.e. **neither** variable is present and non-empty | the existing two-line `Url=` / `ApiKey=` file at `~/.claude/secrets/.divoid-online` |

> **CORRECTION 2026-09-07 (QA #13119 CF-9).** Row 1's selection condition previously read *"`DIVOID_MCP_API_KEY` is present and non-empty after stripping"*. That condition is withdrawn: it made the §5.2 whole-source rule true in one direction only. See the correction note in §5.2 for the falsifying case and the reasoning. Anyone who implemented against the withdrawn condition has a server that silently discards `DIVOID_MCP_URL`.

There is no third source. In particular there is **no** `DIVOID_MCP_CREDENTIALS_FILE` pointer variable: Toni's ruling picks the environment branch as *the* answer and the file as a transitional fallback, so a permanent harness-agnostic file route is a mechanism nobody asked for (§1136 §3 — a knob with no named operator does not ship). The seam is cheap to reopen if the exposure objection in §9 ever becomes concrete: Phase 1 already contains a working file-parsing branch.

### 5.2 A source is used whole

**The selector picks a source; it never merges fields across sources — and the choice of source is made by the presence of *either* environment variable, not by the key alone.** Once the environment branch is selected, both variables are required; a missing or empty one fails closed. The server does **not** fall through to the file for whichever field the environment did not supply, and it does **not** discard an environment field in favour of the file's.

Written as a predicate, since this is the whole rule:

```
env_selected  ==  (DIVOID_MCP_URL non-empty after strip)  OR  (DIVOID_MCP_API_KEY non-empty after strip)
```

**The two falsifying scenarios, which are mirror images of each other.** Both start from the same operator state — a second DiVoid instance being configured on a machine where `~/.claude/secrets/.divoid-online` still holds the production pair — and both end in a request to the wrong host:

| | The operator sets | What a merging (or key-only-selecting) loader does | Consequence |
|---|---|---|---|
| **(i)** | `DIVOID_MCP_API_KEY` (second instance), forgets `DIVOID_MCP_URL` | pairs the second instance's key with the file's production `Url=` | second instance's key sent to production |
| **(ii)** | `DIVOID_MCP_URL` (second instance), and the key does not take — typo'd variable name, host config not reloaded, value not yet pasted | discards `DIVOID_MCP_URL` without a word and uses the file **whole** | **production URL + production key**, while the operator believes the server points at the second instance |

Direction (ii) is the more dangerous of the two, and it is the one the original rule left open. In (i) the mismatched key is very likely rejected by the target — a 401 is noisy and gets investigated. In (ii) **every request succeeds**, against production, with a valid production key, under an operator who believes they are working on a throwaway instance. There is no error to notice. That is precisely the *"wrong-host request that returns 200"* this section was written to prevent, and the key-only selector does not prevent it.

**Why fail closed rather than warn and continue.** Continuing with a `WARNING` that names the ignored variable was considered and rejected on this document's own §8.1 reasoning: a stderr `WARNING` lands in the MCP host's log, *"which nobody tails"*. On the file branch a deprecation `WARNING` is already being emitted, so a second one competes with it on the channel the design has already judged unreliable for reaching a human. Fail-closed costs a half-migrated operator one clear message and a thirty-second fix; warn-and-continue costs a silent write against the wrong host. The asymmetry is not close.

**The legitimate-looking configuration this forbids, and why forbidding it is right.** An operator might reasonably want `DIVOID_MCP_URL` exported from a shell profile (it is not a secret) while the key stays in the `0600` file. The server cannot distinguish that intent from case (ii) — the two produce byte-identical environments. Since one of the two is dangerous and they are observationally identical, the safe reading wins, and the E2 refusal text (§7.2) tells the operator exactly how to get what they wanted: put both in the `env` block. This is the same call §5.2 already made against merging, applied consistently rather than only where the key happened to be the trigger.

**Zero migration is preserved.** Existing installs carry `env: {}` in the host config, so neither variable is present and the file branch is selected exactly as before. The new refusal is reachable only from a state an operator created by setting `DIVOID_MCP_URL`, which no install has today — the variable is introduced by this design.

This remains consistent with Toni's wording. The fallback triggers on *"no secret present"* in the sense that matters — no environment credential configuration at all. It does not trigger on a **partial** environment configuration, in either direction; a half-set environment is a mistake to report, not a state to silently paper over with the file.

> **CORRECTION 2026-09-07 (QA review #13119, CF-9).** The two paragraphs below are the superseded form of this section. They are retained rather than deleted because they were shipped and implemented against: `config.py` at the time of review conformed to them exactly, and any reader who acted on them has a server that silently discards `DIVOID_MCP_URL`.
>
> Withdrawn text, as it stood:
>
> > **The selector picks a source; it never merges fields across sources.** If `DIVOID_MCP_API_KEY` is set and `DIVOID_MCP_URL` is not, the server fails closed — it does **not** fall through to the file for the missing URL.
> >
> > The falsifying scenario this rule exists for: an operator sets `DIVOID_MCP_API_KEY` for a second DiVoid instance while `~/.claude/secrets/.divoid-online` still holds the production `Url=`. Under a merging loader the server would silently send the second instance's key to production. Under whole-source selection it refuses to start and says which variable is missing. Fail-closed beats a wrong-host request that returns 200.
>
> **What falsified it.** QA demonstrated the mirror case by execution: with `DIVOID_MCP_URL` set to a second instance and `DIVOID_MCP_API_KEY` absent, `load_secret` returned the production base URL and the production key from the file, discarding `DIVOID_MCP_URL` silently. The defect was in this section, not in the implementation.
>
> **Why it was wrong, stated as the general lesson.** The withdrawn text named its rule *"a source is used whole"* but wrote its predicate over **one field of one source**. A whole-source rule whose selector reads a single field is only half a rule; the direction it does not read is exactly the direction nobody thinks to test. This document already knew the mechanism — §8.2 names *"a typo'd variable name leaves the config looking migrated while the server silently falls back"* as a reason to reject a Phase-2 check — and did not carry that knowledge back into §5.2. Naming a hazard in one section is not the same as closing it in another.

### 5.3 Variable names

`DIVOID_MCP_URL`, `DIVOID_MCP_API_KEY` — the `DIVOID_MCP_` prefix already used by `DIVOID_MCP_FILE_ROOT` (`paths.py:14`) and `DIVOID_MCP_LOG_LEVEL` (`server.py:36`).

Rejected: the unprefixed `DIVOID_URL` / `DIVOID_KEY`. *(Measured 2026-09-07.)* Those two names are a live, documented shell convention — `grep -rn "DIVOID_URL\|DIVOID_KEY" divoid-mcp/ --include=*.py --include=*.md` returns eleven rows across five files: `phase-1.md:10-12`, `drift-policy.md:35-37,45`, `version.py:10`, `tests/smoke/README.md:89-91`, plus the raw-REST recipe in the machine-global `CLAUDE.md` outside this repo. The prefix makes the collision impossible and makes the README's configuration section read with one convention instead of two.

**The collision is not hypothetical, and the enumeration is named so it stays re-runnable.** *(Measured on this machine, 2026-09-07, by the orchestrator; no values recorded here or anywhere — the variable **names** are the finding.)* Enumerated across the process environment, Windows **user** and **machine** environment variables, `~/.bashrc`, `~/.bash_profile`, `~/.profile`, `~/.bash_login`, and PowerShell profiles:

| Variable | State on this machine |
|---|---|
| `DIVOID_MCP_URL` | **Set nowhere** — no process, user, or machine variable, no shell profile, no PowerShell profile (none exists). |
| `DIVOID_URL` | **Set**, as a persistent Windows **user** environment variable, pointing at the production instance. |
| `DIVOID_KEY` | Not set. |
| `DIVOID_KEY_PEPPER` | Set. Unrelated — the backend's API-key pepper, not a divoid-mcp input. |

Two consequences, and the second is the one that makes this a paragraph rather than a footnote.

**(i) §5.2's refusal does not fire here.** `DIVOID_MCP_URL` is unset in every location checked, so this machine takes the file branch exactly as before. That is half of §16 item 2, answered.

**(ii) Had the variables been named `DIVOID_URL` / `DIVOID_KEY`, every MCP session on this machine would refuse to boot.** The symmetric selector would find `DIVOID_URL` set at every start, enter the environment branch, find no key, and **fail closed with E2** — not "switch credential source", *refuse*. The prefix decision in this section is load-bearing against a collision that is **real and present**, not anticipated.

> **And the prefix became *more* load-bearing when CF-9 was fixed, which is the counter-intuitive half.** Under the original key-only selector the same collision was a **silent ignore**: the selector read only `DIVOID_KEY`, found it absent, and fell back to the file while discarding `DIVOID_URL` — which is precisely hazard direction (ii) in §5.2, firing on this machine, every session, unnoticed. Fixing CF-9 converted that silence into a hard refusal. **A correctness fix can raise the cost of an unrelated naming mistake**, because it turns a class of misconfiguration from ignored into fatal; the two decisions were taken independently and interact.

> **CORRECTION 2026-09-07 (QA #13153, W-1).** The withdrawn form of this passage read: *"If an operator **ever exports** them from a shell profile so their curl recipes work, every MCP session on the box would silently switch credential source."* Two defects, both retained as instances of patterns this document names elsewhere. **The tense was anticipatory for a condition that was already true** — `DIVOID_URL` was already exported when that sentence was written, so the hypothetical was describing the present (§12's (C2) probe hunts exactly this shape, and did not catch it because `would` is not in its pattern). **And the claim was asserted inside a document that established the measured/asserted marking discipline in §9** — the marking convention was applied to §9 and not carried to the rest of the document. Both are now measured and marked.


### 5.4 Value handling

Both environment values are `.strip()`ped before use, matching the per-line `.strip()` the file parser already applies at `config.py:59`. A trailing newline pasted into a host config is a plausible and otherwise very confusing failure (it produces a 401, not a startup error).

An **empty-string** value counts as absent, **for both variables symmetrically**: the selector in §5.2 tests each variable's stripped value for non-emptiness, so `DIVOID_MCP_API_KEY=""` with no URL falls back to the file, and `DIVOID_MCP_URL=""` with no key does the same. A whitespace-only value collapses to the empty case by the same `.strip()`. Membership (`"DIVOID_MCP_API_KEY" in env`) is the wrong test in either position: MCP hosts write `"env": {}` and some write empty strings for unset keys, and Toni's criterion is *"no secret present"*.

The asymmetry that remains is deliberate and small: an empty value is *absent*, a non-empty value is *present*. There is no third state, so no configuration can be simultaneously "set enough to select the environment" and "not set enough to be required".

### 5.5 Shape of `load_secret`

```
load_secret(env=None, path=LEGACY_SECRET_FILE_PATH) -> DivoidConfig
```

- `env` defaults to `os.environ` when `None`, mirroring `paths.init(env=…)` at `paths.py:42-47` — that is the house pattern for a testable environment read, and reusing it means `test_config.py` needs no new fixture idiom.
- `path` keeps its existing role (test injection). Both existing callers pass no arguments (`server.py:51`, `tests/smoke/run_all.py:82`), so the added leading keyword parameter breaks nothing.
- Internally: `_from_env(env)` and `_from_fallback_file(path)`, with the selector in `load_secret`. **`_from_env` reads and strips both variables itself** — it is not handed a pre-resolved key, because under §5.2's predicate the branch can be entered with the key empty and the URL set, and that state is E2's job to report rather than the selector's to pre-empt. The current parse body moves into `_from_fallback_file` essentially unchanged. **This split is the Phase-2 seam**: Phase 2 deletes `_from_fallback_file`, the constant, and the selector, and nothing else moves.
- `SECRET_FILE_PATH` is renamed `LEGACY_SECRET_FILE_PATH`. The rename makes the deprecation legible in the code and makes Phase 2's deletion a grep. No site outside `config.py` references the old name (`grep -rn "SECRET_FILE_PATH" divoid-mcp/` → `config.py:22`, `config.py:31` only).

### 5.6 `DivoidConfig` gains one non-secret field

```
DivoidConfig(base_url: str, api_key: str, source: str)
```

`source` is `"env"` or `f"file:{path}"`. It carries **no secret** — a path or a literal — and it exists to do two jobs at once (§8): it drives the conditional deprecation sentence in the MCP `instructions`, and it is what the startup log line reports.

This respects the boundary stated in concept #6108 (*"`paths.py` is deliberately outside `DivoidConfig`, which is the secret container"*): that boundary is about not widening where the **key** lives. A non-secret descriptor of where the key came from does not widen it, and it rides the channel that already exists — `mcp_server.config` is already attached to the FastMCP object at `server.py:80`. The alternative, a module-level `config.credential_source()` accessor in the `paths.roots()` style, is a second mechanism (module global + function) for one string.

## 6. Decision 2 — how the requirement is declared

Toni's acceptance criterion is *"knows what it has to create by looking at the mcp (not code)"*. Concretely, a stranger learns the contract by one of these, in the order they will actually meet them:

| # | What they do | What they get |
|---|---|---|
| 1 | Register the server and start it with nothing configured | The fail-closed refusal in §7 — names both variables, the copy-pasteable `claude mcp add` line, and the doc pointer. This is the artifact that reaches an environment which knows nothing about this project. |
| 2 | `claude mcp list` shows `Failed to connect`, so they run `python -m divoid_mcp` directly | The same refusal. This route is already documented at `docs/install.md:137-147`. |
| 3 | Open `README.md` | A rewritten **Configuration** section leading with the two variables and the `env` block, with the file documented below it as the deprecated fallback. |
| 4 | Open `docs/install.md` (= DiVoid #829, the human-facing install guide) | Step 2 rewritten to teach the `env` block first. |
| 5 | Copy `examples/.mcp.json` | A registration example whose `env` block already contains both variable names alongside the existing `DIVOID_MCP_LOG_LEVEL`. |

### 6.1 Why not a registry `server.json`

Task #13105 nominates the MCP registry `server.json` schema — whose `environmentVariables` entries carry `name` / `description` / `isRequired` / `isSecret` / `default` — as the standard declaration artifact. It is the right *standard*, and it is the wrong *instrument here*. Three independent reasons, any one of which is sufficient:

1. **It has no consumer.** Recursing the chain per §1136 §2: `server.json` → the MCP registry → divoid-mcp is not published to it and no task proposes publishing it. Dead end at step two. That is the Form-1 data dump the contract names.
2. **It cannot be filled honestly.** `environmentVariables` is a property of a `packages[]` entry, and `registryType` is an enum of `npm | pypi | oci | nuget | mcpb` (verified against `https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json`). divoid-mcp is installed with `pip install "git+https://github.com/telmengedar/DiVoid.git#subdirectory=divoid-mcp"` (`README.md:12`, `docs/install.md:24`) and is on none of those registries. Omitting `packages` is schema-valid — top-level `required` is `["name","description","version"]` — but a `server.json` without `packages` has nowhere to put `environmentVariables`, which is the only reason to write the file.
3. **The install path never delivers it.** `[tool.hatch.build.targets.sdist].include` is `["src/", "tests/", "docs/", "README.md", "LICENSE"]` (`pyproject.toml:35`) and the wheel packages `src/divoid_mcp` only (`pyproject.toml:32`). A repo-root `server.json` reaches nobody who installs the way this project installs.

**What survives from the standard:** its *answer*, not its file. Publishing divoid-mcp to the MCP registry — at which point `server.json` becomes the correct and consumed declaration — is a named future in §10, not a member built now.

### 6.2 Also rejected: a `--check-config` / `--help` CLI mode

A flag that prints the contract without provoking a failure. It buys nothing over route 1 above, which is free and already documented, and it adds argument parsing to a module that currently has none (`__main__.py` is nine lines).

### 6.3 Keeping the vocabulary from drifting across sites

The two variable names appear in the code once each as module constants and are interpolated everywhere else:

```
ENV_URL: Final[str] = "DIVOID_MCP_URL"
ENV_API_KEY: Final[str] = "DIVOID_MCP_API_KEY"
```

matching `paths.py:14`'s `ENV_VAR: Final[str]`. The shared hint block in §7.1 is a real DRY case: **10 rendered lines × 8 sites that append it = 80** (E1, E2, F1–F5, and the §8.1(a) deprecation warning), far above the ~15–20 threshold of #1267, so it extracts to one module constant rather than being repeated per branch. The remaining duplication is documentation prose (README, install.md, examples) — not code, and not extractable across a Markdown file and a JSON file; the mitigation is that all of them are named in the change inventory in §12 so one PR touches them together.

## 7. Decision 3 — the fail-closed diagnostic

The refusal is the primary discovery instrument (§6, route 1), so its text is a deliverable, not an afterthought. All of it goes through `logger.error` to **stderr** (`server.py:38-42` configures logging before `load_secret()` is called at `:51`), followed by `sys.exit(1)` — the fail-closed property from #6108 §1 is preserved exactly: the process dies before `run_stdio_async`.

Source-level note for the implementer: `.editorconfig` sets `max_line_length = 100` for `*.py`, so these strings are written as implicitly-concatenated literals; the **rendered** text is what is specified below.

### 7.1 The shared hint block

Appended as the last paragraph of every branch below, defined once as `_ENV_HINT`:

```
divoid-mcp takes its credentials from the environment (MCP specification 2025-06-18,
Authorization: stdio servers retrieve credentials from the environment). Set both of these
in the "env" block of this server's entry in your MCP client configuration:
    DIVOID_MCP_URL      the DiVoid API base URL, including the /api suffix
                        (for the mamgo instance: https://divoid.mamgo.io/api)
    DIVOID_MCP_API_KEY  your DiVoid API key
In Claude Code:
    claude mcp add --transport stdio --scope user divoid \
      -e DIVOID_MCP_URL=<url> -e DIVOID_MCP_API_KEY=<key> -- python -m divoid_mcp
Full instructions: divoid-mcp/docs/install.md (DiVoid node #829).
```

It appears in **every** failure branch, including the malformed-file ones. A user whose file lost its `ApiKey=` line must learn that the environment route exists at that moment — otherwise the deprecated path is the only one they will ever know about, which is the exact failure this task is closing.

### 7.2 The branches

`{path}` renders the resolved fallback path; `{err}` the OS error text.

**E1 — environment selected, URL missing** (`DIVOID_MCP_API_KEY` set, `DIVOID_MCP_URL` empty or absent):

```
DIVOID_MCP_API_KEY is set but DIVOID_MCP_URL is empty or missing -- divoid-mcp cannot start.
When either DIVOID_MCP_URL or DIVOID_MCP_API_KEY is set, the environment is the credential
source and both variables are required; the deprecated fallback credentials file is not
consulted.
<_ENV_HINT>
```

**E2 — environment selected, key missing** (`DIVOID_MCP_URL` set, `DIVOID_MCP_API_KEY` empty or absent). This is the branch added by the §5.2 correction, and its second paragraph is the whole point of it: the operator's mental model says the server is pointed at the URL they set, and they must learn that the file was deliberately *not* used rather than conclude the server is broken:

```
DIVOID_MCP_URL is set but DIVOID_MCP_API_KEY is empty or missing -- divoid-mcp cannot start.
When either DIVOID_MCP_URL or DIVOID_MCP_API_KEY is set, the environment is the credential
source and both variables are required. The deprecated fallback credentials file at {path}
was NOT consulted, even if it exists -- using its key with the DIVOID_MCP_URL set here would
pair credentials from two different sources, which is how a request reaches the wrong host.
The most common cause is a misspelled variable name: check that the key is spelled exactly
DIVOID_MCP_API_KEY in the same "env" block.
<_ENV_HINT>
```

**Why E1 and E2 are two literal messages and not one parameterised template.** `block_size × site_count` = ~4 rendered lines × 2 sites = **8**, below the ~15–20 threshold of #1267, so a shared helper does not earn its keep and the KISS deletion test (*"can the new helper be three lines at the call site?"*) answers yes. The two messages are also not the same message with a variable swapped: E1's operator forgot a field, E2's operator's key did not take, and the diagnosis differs (E2 names the typo cause and states the non-consultation of the file). Parameterising would flatten the more useful half. **Both are written inline at their branch.**

**F1 — nothing configured** (**neither** environment variable set, and no file). This is the headline message and the one a foreign environment meets:

```
No DiVoid credentials found -- divoid-mcp cannot start.
Neither DIVOID_MCP_URL nor DIVOID_MCP_API_KEY is set, and the deprecated fallback
credentials file was not found at {path}.
<_ENV_HINT>
```

> **CORRECTION 2026-09-07, amended after QA #13150 (CF-F) — a record, not an instruction.** Two things were superseded here and both are now done.
>
> **The label** previously read *"(no key in the environment, no file)"*, naming half the precondition: under §5.2's corrected predicate F1 fires only when neither variable is set.
>
> **The message body** previously read *"`DIVOID_MCP_API_KEY` is not set, and the deprecated fallback credentials file was not found at {path}."* — the same half-precondition, in the branch that documents the selector. It was **not false** (the key genuinely is unset there, and `_ENV_HINT` immediately after names both variables), but it was pre-CF-9 wording. **It now names both variables, in the code and in the fence above**, and `test_config.py` pins the shipped wording.
>
> **Why this block was itself a defect for one round, which is the reusable part.** Its previous form closed with *"Flagged for the implementer rather than silently left: the message body **should** name both variables as unset"* — future-imperative, describing work that was already finished, sitting directly above a fence still showing the superseded text. #114's Addendum 2026-08-27 names that tell exactly: *anticipatory tense in prose next to shipped code almost always means it was written before the change and never revisited.* The concrete hazard was not confusion but **reversion**: the plausible action for a reader who trusts the fence is to "fix" the code back toward it, undoing the remediation.
>
> **It is also a third instance of the failure this subsection diagnoses roughly sixty lines below** — *"adjacent contradiction is this document's characteristic failure"* — in the same subsection, in the same edit that wrote the diagnosis. Recorded rather than smoothed, because the diagnosis being right did not make it self-applying: **naming a failure mode is not a check for it.** The check has to be mechanical, and it now is — §12's instruments cover this document (CF-G there).
>
> **The general form: a design fence is a claim about shipped text, and it decays like any other claim.** When an implementer is told to change a user-facing string, the fence quoting that string is a **site of the change**, not a specification standing outside it.
**F2 — fallback file unreadable** (`PermissionError` / `OSError`):

```
Cannot read the fallback credentials file {path}: {err} -- divoid-mcp cannot start.
<_ENV_HINT>
```

**F3 — fallback file empty:**

```
The fallback credentials file {path} is empty -- divoid-mcp cannot start.
<_ENV_HINT>
```

**F4 — fallback file missing a line** (one message, parameterised by `Url=` or `ApiKey=`):

```
The fallback credentials file {path} is malformed: no '{prefix}' line -- divoid-mcp cannot start.
<_ENV_HINT>
```

**F5 — fallback file has an empty value** (one message, parameterised by `Url` or `ApiKey`):

```
The fallback credentials file {path} has an empty '{key}=' value -- divoid-mcp cannot start.
<_ENV_HINT>
```

F4 and F5 each replace two literal branches (`config.py:65-79`), and F2 merges the duplicate `PermissionError` / `OSError` pair (`config.py:44-49`). The file path's **eight** `logger.error` sites at `7700ebb` (`config.py:42, 45, 48, 52, 66, 70, 74, 78`) become **five** message templates for the file branch (plus E1 and E2 for the environment branch), while the information content rises.

**F4 and F5 are one *text* each: one module-level `Final[str]` template constant per message, `.format()`ed at each of its two call sites, with no helper function.** There are **three** shapes available here, not two, and every previous revision of this paragraph collapsed them into a binary:

| Shape | One text? | Indirection | Verdict |
|---|---|---|---|
| A private helper — `_malformed_file(path, prefix)` | yes | a function per message | **No.** The wrapper's own math is ~3 rendered lines × 2 sites = **6**, below #1267's ~15–20, so it does not earn its keep (#114 §0, #1136 §1). |
| An f-string written inline at each branch | **no — two texts that merely match today** | none | **No.** This is what the previous revision prescribed, and it defeats the goal in the row below. |
| **A module-level `Final[str]` template, `.format()`ed at each branch** | yes | none | **Yes.** One text, parameterised, no function. |

**The goal that decides it, stated before the mechanism rather than after:** writing the malformed-line wording **once**, parameterised by `'Url='` / `'ApiKey='`, is what stops four messages drifting into four different phrasings. Only the third shape delivers that — editing the wording becomes a single-site edit by construction, and mutating the constant reddens both of its dependent tests. `_fail` remains `NoReturn` under any of the three, so the type-narrowing after the guards is unaffected and is not a discriminator.

**E1 and E2 are not this situation** and correctly remain two separate inline literals. The difference is the *unit*, not the line count: F4 is **one message rendered with two different arguments**, whereas E1 and E2 are **two different diagnoses that happen to share a sentence**. The one extraction this design authorises on span alone is `_ENV_HINT`, whose math is quoted in §6.3 and is an order of magnitude over the threshold.

> **CORRECTION 2026-09-07 (QA review #13145, CF-A) — the second correction to this same paragraph.** Withdrawn text, as it stood *after the first correction*:
>
> > *"**F4 and F5 are one text each, written inline at both of their call sites** — not extracted into a helper function."* … *"Each branch calls `_fail(...)` **directly with the f-string**."*
>
> Retained because it was implemented against literally: the implementer produced four independently-worded f-strings and had to be walked back. **The code was right and this paragraph was wrong** — the shipped module-level constants are a third shape this document had never named.
>
> **Why the same four lines have now been mis-specified in two consecutive rounds, in two different ways.** This is the part worth carrying forward; without it a third round of the same shape is the expected outcome.
>
> 1. **The decision space has three points and the paragraph kept reasoning over two.** #1267's threshold answers *"should this be a function?"*. That binary was imported wholesale and allowed to also answer *"where does the text live?"* — a different axis, governed by drift risk rather than by line count. With only *extract* and *inline* on offer, the shape that wins on both axes has no slot: it went unnamed in round 1 and was actively excluded in round 2.
> 2. **The instrument adopted to fix round 1 is what encoded round 2's error.** CF-6's remedy was *"quote the math for every extract/inline decision"*. A math table has exactly two outcomes — above threshold, below threshold — so it structurally coerces every decision into that binary. **A checklist that demands a number per decision will silently push decisions whose real axis is not the one the number measures.** That generalises well past this document, and it is the reason the remedy below is a change to the table's *shape* rather than to its cell values.
> 3. **Nothing ever checked a mechanism against the goal stated one bullet above it.** Both rounds' errors share that surface: round 1's *"no inline-at-N-sites decision is taken anywhere"* sat beside the `_ENV_HINT` math that is one; round 2's *"inline f-string"* sat beside *"writing the wording once"*, which it defeats. Adjacent contradiction is this document's characteristic failure, and it is invisible to every downstream check — QA ruling 2 records that **no mutation could have caught the resulting regression**, because four identical f-strings and one shared constant are behaviourally indistinguishable. It was catchable only by reading, which is exactly why the design must prescribe the shape it wants.
>
> **The structural fix, not merely the text fix:** §14's table now carries a **Unit — what the number decides** column, so a row whose number answers a different question than its own verdict is visible inside the row. The three-shape table above replaces the binary here, so the missing third option cannot silently re-form.

### 7.3 The startup success line

Replaces `config.py:82`. Reports the source; still never the key:

```
Config loaded: source=env base_url=https://divoid.mamgo.io/api key=***
Config loaded: source=file:C:\Users\<you>\.claude\secrets\.divoid-online base_url=... key=***
```

### 7.4 The 401 hint

`errors.py:67` currently names one hardcoded path, which is wrong under any new contract. It becomes source-agnostic — `map_http_error` has no access to `DivoidConfig` and threading one in would be plumbing for a hint string, while the startup line in §7.3 already tells a troubleshooter which source won:

```
{prefix}DiVoid rejected the request as unauthorized (401). The API key may be invalid or
expired. Check the credentials this server was started with -- DIVOID_MCP_API_KEY in the
MCP client's env block, or the fallback credentials file. The startup log line
"Config loaded: source=..." names which one was used.
```

## 8. Decision 4 — retiring the fallback

Phase 2 deletes the fallback, so Phase 1's job is to make sure no operator is still silently standing on it when that happens.

### 8.1 Where the deprecation surfaces

**(a) A startup `WARNING` on stderr**, emitted only on the file branch, immediately before the §7.3 success line:

```
DEPRECATED credential source: divoid-mcp started from the fallback credentials file {path}.
This fallback will be removed. Move the credentials into the "env" block of this server's
entry in your MCP client configuration.
<_ENV_HINT>
```

**(b) One sentence appended to the server's MCP `instructions`**, again only on the file branch. Today's instructions are built inline at `server.py:71-76`; this design extracts that into `_build_instructions(config) -> str` in `server.py` so it is callable from a unit test without entering the stdio loop. The appended sentence, verbatim — its exact wording is load-bearing, because it is the string the Phase-2 check looks for:

```
 NOTE: this server started from the DEPRECATED credentials file fallback; ask your operator to move the DiVoid credentials into the MCP client's env block (DIVOID_MCP_URL / DIVOID_MCP_API_KEY) before that fallback is removed.
```

**Why (b) exists when (a) already does.** A stderr WARNING lands in the MCP host's log, which nobody tails; on the delete-it check, removing (b) leaves the deprecation with no channel that reaches a human, and Phase 2 then breaks people silently. The `instructions` string is delivered to **every agent in every session** — on this machine, that is the channel that actually reaches an operator. It is one conditional fragment on an existing surface, it carries one short sentence, and it is absent entirely when the environment source is used, so a migrated install pays nothing.

**(c) Documentation** — `README.md`, `docs/install.md`, `examples/.mcp.json`, `divoid-mcp/CLAUDE.md` (see §12).

### 8.2 The observable that opens Phase 2

> **Phase 2 opens when every machine on the roster reports the §8.1(b) sentence absent from the divoid server's instructions.**

The check, per machine, is: ask the agent in a fresh session whether the divoid server's instructions mention the deprecated credentials file fallback. It is a yes/no, it involves no secret value, and it observes **what the server actually resolved** rather than what a config file claims.

Deliberately *not* the check: `claude mcp get divoid` showing the variables in its `Environment:` section. Two reasons — config presence is not proof the server took that branch (a typo'd variable name leaves the config looking migrated while the server silently falls back, which is exactly case (ii) in §5.2), and **`claude mcp get` prints environment values in full plaintext** (measured; see §9), so running the check would itself put the API key into a transcript, which is not remediable by deletion.

> **CORRECTION 2026-09-07 (QA review #13119, CF-1).** The second reason above previously read:
>
> > *"…and it is unmeasured whether that command prints environment **values**; if it does, running it puts the API key into a transcript, which is not remediable by deletion."*
>
> It was measured on 2026-09-07 (probe transcript in §9) and the conditional is now unconditional. The withdrawn clause is retained because it was shipped: a reader who acted on it may have believed the transcript hazard was speculative and run `claude mcp get` on a migrated install to check their configuration. If you did, treat that terminal output and any transcript of it as containing the key in the clear, and rotate.
>
> The clause is patched here rather than only corrected in §9, because an appended section does not un-say the body above it and §8.2 is the higher-salience half for a reader scanning the Phase-2 gate (#114 Addendum 2026-08-29: *append **and** patch, never append alone*).

Also deliberately not built: any telemetry, counter, or phone-home. The population is *"currently its just our team, so not a lot"* — an enumerable roster, not a statistical one. §1136 §3's telemetry-then-tune compound is exactly what a metric here would be.

Phase 1 files a DiVoid task for Phase 2, linked to this document and to #13105, holding the roster (this machine, Paul's, and any team member who followed `docs/install.md`) with a checkbox per operator. The roster lives in that task, not in this document, because it changes as people are added.

## 9. What the environment source widens, and what it does not

Key containment invariant 1 (`divoid-mcp/CLAUDE.md:19`, concept #6108 §1) states the raw key lives only in the frozen `DivoidConfig` and the pre-built `Authorization` header at `http_client.py:55`. Under an environment source, that sentence is **no longer complete**, and the honest restatement is:

> The raw key value is read once from its configured source and thereafter lives only in `DivoidConfig` and the pre-built `Authorization` header. Under the environment source, the source itself is the process environment for the lifetime of the process.

What that costs, concretely:

| | Fallback file (today) | Environment (Phase 1 primary) |
|---|---|---|
| At rest | `~/.claude/secrets/.divoid-online`, user-owned | `~/.claude.json`, user-owned — same home directory, same ACLs |
| In the process | `DivoidConfig` + the header | `DivoidConfig` + the header + `os.environ` |
| Inherited by children *(measured, §2)* | n/a | none — divoid-mcp spawns no child process |
| Readable by other processes *(**asserted, not measured**)* | file read requires the user's rights | `/proc/<pid>/environ` is owner-only on POSIX; on Windows reading another process's environment requires `PROCESS_VM_READ`. Same-user, in both cases. |
| New exposure | — | host tooling that prints server configuration, and crash dumps that capture the environment |

**Measured, not hypothetical: `claude mcp get <name>` prints environment values in full plaintext.** DiVoid task #13105 left this as an open question — whether that command shows environment *keys only* or *values* — because Sarah could not observe it (the `Environment:` block was empty on the drafting machine) and correctly declined to plant a dummy value to find out. It has since been measured: a throwaway local-scope server was registered with `-e PROBE_PLAIN=visible-value-abc123`, `claude mcp get` was run, and the server was removed. Output:

```
  Environment:
    PROBE_PLAIN=visible-value-abc123
```

**What this does and does not change about the "New exposure" row.** It does **not** widen the ACL: the caller must already be able to run `claude` as that user, which is the same principal who can `cat` the `0600` fallback file, so on the table's own access axis the two sources still sit at the same bar. What it adds is a **different kind** of exposure that the file does not have — the key value lands in **terminal output, and therefore in scrollback, terminal logs, screen shares, and agent transcripts**, where it is not remediable by deleting anything. A file has to be read deliberately; a printed value propagates on its own afterwards. That is the load-bearing half, and it is what `install.md:44` warns about: an operator who puts a live key into a host's `env` block must know that `claude mcp get` echoes it back in the clear.

This confirms §8.2's rejection of `claude mcp get divoid`'s `Environment:` section as the Phase-2 check. It is the **same** reason §8.2 already gave — the transcript hazard — now stated unconditionally rather than conditionally; it is not an additional reason, and §8.2 has been patched to the measured form in the same edit that carries this paragraph.

**The complementary measurement: `claude mcp list` does *not* print environment values.** Measured 2026-09-07 by the same probe shape with one word changed — a throwaway local-scope server registered with `-e PROBE_SENTINEL=zzsentinelzz991` and command `echo hi`, `claude mcp list` run, its full output grepped for the sentinel, the entry removed. Sentinel count **0**, and the entry rendered as a single line carrying name, command and connect status with **no `Environment:` block at all**:

```
_probe_listenv: echo hi - ✘ Failed to connect — CONNECTION_CLOSED: Connection closed
```

**So the two commands split cleanly, and that split is the operative fact: `list` verifies, `get` discloses.** This makes `docs/install.md:70-74` correct by evidence rather than by luck — it already sends every reader to `claude mcp list` as the post-install verification step, which is the non-disclosing half of the pair. **The install documentation states this positively rather than leaving it as an absence**, because the next person to touch that step will otherwise reach for `claude mcp get` as the more informative option and hand the reader a command that echoes their key back in the clear. A step that is safe for a reason nobody wrote down is one edit away from not being safe.

> **CORRECTION 2026-09-07 (QA review #13119, W-8/W-4).** The paragraph above previously overstated its evidence in two places, both retained here because they were shipped inside a block headed *"Measured, not hypothetical"*:
>
> > *"it is also **anyone who runs one ordinary CLI command**, `claude mcp get divoid`, with no elevated access and no crash-dump collection required"* — withdrawn as an ACL claim; the ACL is unchanged, and the real delta is the transcript persistence now stated above.
> >
> > *"`claude mcp list` does not show this (it prints connection status only)"* — **withdrawn as unmeasured on 2026-09-07, and re-established by measurement the same day** (probe and result in the paragraph above). The claim turned out **true**. That does not retire the finding, and the distinction is the whole lesson: what was defective was the *confidence*, not the content. An unevidenced sentence sat inside a block headed *"Measured, not hypothetical"*, and nothing in the document separated it from the sentence beside it that had a transcript attached. **A claim that reads as measured and is not is a defect whichever way it later resolves** — had this one resolved the other way, a documented verification step would have been disclosing keys under a heading asserting it had been checked.
>
> **This mattered beyond the sentence.** `docs/install.md:70-74` sends every reader to `claude mcp list` as the post-install verification step, so a safety property of the documented verification path rested on an unmeasured claim. It has since been measured and lands safe — which is the fortunate outcome, not the general one, and is why the probe is recorded rather than the conclusion alone. **Re-runnable form**, for whenever the CLI's output changes: register a throwaway local-scope server carrying `-e PROBE_SENTINEL=<sentinel>` with a trivial command, run `claude mcp list`, grep the **full** output for the sentinel, remove the server. It must be run against a **dummy** value — running it against the live `divoid` entry is the hazard itself. Tracked as §16 item 1, now closed.

**Marking convention for this section, added 2026-09-07 (QA #13145, W-f).** §9 is the one section a reader now expects to be evidenced, so every claim in it is marked. A row or sentence carries *(measured)* with its probe, or *(**asserted, not measured**)*. The two platform rows above are asserted — neither this document nor QA verified them, and the design does not rest on them: the ACL argument was withdrawn precisely because the exposure that matters is transcript persistence, not process-memory access. **A positive-only discriminator is only half a discriminator** — marking what was measured while leaving everything else unmarked reproduces, in miniature, the failure the correction block below records.

**Not doing:** popping `DIVOID_MCP_API_KEY` out of `os.environ` after reading it. *(Measured 2026-09-07: `grep -rn "os.environ\|getenv" divoid-mcp/src/` returns exactly `config.py:70`, `paths.py:47`, `server.py:37`.)* There is no in-process reader of the environment beyond `paths.py:47` (`DIVOID_MCP_FILE_ROOT`) and `server.py:37` (`DIVOID_MCP_LOG_LEVEL`), and no tool that reflects the environment — so popping would be a guard against code that does not exist (§1136 §6, defensive code for impossible scenarios). It also would not touch the dominant term, which is the key sitting in the host's config file. The exposure is **stated** rather than defended against, and `divoid-mcp/CLAUDE.md:19` is updated to the restated wording above so the invariant stays true.

**Unchanged:** the key is still never logged (`key=***`), never returned by a tool, and still redacted out of error bodies by `errors._redact` (`errors.py:99-103`). The new `source` field carries a path or the literal `"env"`, never a value.

## 10. Phase 2 — a named future, in prose

Phase 2 is **not specified as members here**. There is no version gate, no removal-date constant, no `deprecated=True` flag, and no vocabulary for it in Phase 1's code — per the template's 2026-08-17 rule that a future phase is a list in prose, never a declared member.

In prose, then: Phase 2 deletes `_from_fallback_file`, `LEGACY_SECRET_FILE_PATH`, the selector in `load_secret`, the deprecation `WARNING`, the conditional sentence in `_build_instructions`, and the `file:` value of `DivoidConfig.source` — at which point `source` itself probably stops earning its place and goes too. The error set collapses to E1, E2, and a single no-credentials message — E1 and E2 survive Phase 2 unchanged, because "both variables are required" is the permanent contract, not a transitional one; only their *"the fallback file was not consulted"* clauses go. The documentation sites in §12 lose their fallback paragraphs.

What Phase 2 does **not** do is delete `~/.claude/secrets/.divoid-online`; see §4.

Beyond Phase 2, and further out: if divoid-mcp is ever published to the MCP registry, `server.json` becomes the correct and consumed declaration artifact and §6.1's objections all lapse. If a genuine need for a file-based credential source outside `~/.claude` ever appears, the file-parsing branch is the seam. If divoid-mcp ever grows an HTTP transport, the OAuth branch of the spec becomes binding. None of these is scheduled and none of them gets a declaration now.

## 11. Q5 — the HTTP / OAuth branch is out of scope, not deferred

The spec is explicit that stdio implementations **SHOULD NOT** follow the authorization specification. Adopting OAuth would mean first adopting an HTTP transport — a listening port, a deployment target, a redirect URI, an authorization server, and a resource-metadata document — none of which this task, this server, or DiVoid's own API-key authentication asks for. It is not a "later phase" of this design; it is a different feature that would supersede the transport decision recorded in `docs/architecture/phase-1.md:32` and node #695. It is named here so a reader does not go looking for it, and it gets no seam, no flag, and no configuration knob in Phase 1.

## 12. Change inventory

**Re-derivation rule** (use these rather than trusting the list — an enumeration in a living document may list, but never assert coverage). There are **three** commands: two because this design has two ripples with disjoint vocabularies, and a third because **this document is itself an artifact the other two must be able to reach.**

> **Scope is part of an instrument, and it is the part that fails silently.** Every command below states the paths it covers. A sweep run on the wrong paths returns a clean result that is **evidence of nothing**, and it reads exactly like a clean result that means something — which is worse than an un-run sweep, because it arrives with an execution behind it. **The path most likely to be omitted is the one the author is standing in:** all three commands here originally covered `divoid-mcp/` only, while this document lives at repo-root `docs/architecture/`, so for four rounds no instrument could see the file that contains them. That is the whole reason CF-F survived four clean sweeps. See the standing rule at the end of this section.

**(A) The credential-vocabulary sweep** — finds prose and code that names the old single-source contract:

```
grep -rn "divoid-online\|secret file\|Secret file\|SECRET_FILE\|fail-closed\|Fail-closed" \
  divoid-mcp/ docs/architecture/divoid-mcp-credential-contract.md \
  --include=*.py --include=*.md --include=*.json --include=*.toml \
  | grep -v '.venv\|__pycache__'
```

Every hit is either patched per the tables below or listed under **Deliberately not changed**. Per #114's 2026-08-28 addendum, **for every hit this sweep CLEARS rather than patches, re-derive the claim from the post-diff source** — read the line as it now stands, do not reason from the change you believe you made. A wrong clearance is worse than a missed one, because it arrives with an argument attached.

**(B) The constructor-signature sweep** — finds the `DivoidConfig(...)` call sites forced by §5.6's third required field:

```
grep -rn "DivoidConfig(" divoid-mcp/ --include=*.py \
  | grep -v '.venv\|__pycache__' | grep -v "source="
```

**(B) is a screen, not a gate, and the distinction is the point.** Its passing condition is **"every row has been read, and no row is a construction site lacking `source=`"** — *not* "zero rows". Rows are expected: a `DivoidConfig(` call whose `source=` sits on a continuation line cannot be seen by the line-oriented `grep -v`, so it surfaces as a false positive. Read each row and clear it against the source; a cleared row costs seconds. Against the tree at the time of writing it returns **two** rows, both multi-line calls in `tests/unit/test_server.py` that do carry the field.

**What (B) does guarantee, and it is the property that matters:** it has **no false negatives within the paths given** — a qualifier the original claim omitted, and the omission is CF-G's subject. A construction site genuinely missing `source=` surfaces as a row whether it is written on one line or five. A screen with no false negatives is a sound instrument for *finding*; it is simply not a predicate that decides on its own.

**Do not "fix" the false positives with a multiline matcher.** `rg -U --multiline-dotall 'DivoidConfig\([^)]*\)' … | grep -v "source="` is the obvious improvement and it is **measurably worse**: ripgrep emits a multi-line match as multiple output lines, the filter then removes only the middle line that carries `source=`, and the same two call sites come back as **four** rows instead of two. Measured, 2026-09-07. Rust's regex engine has no lookahead, so no single-pattern variant fixes this; genuine decidability needs an AST walk, which is a tool nobody asked for on a one-time sweep (#1136 §3). The line-oriented command with an honest label is the better instrument.

> **CORRECTION 2026-09-07 (QA review #13145, CF-B).** The sentence replaced above read: *"**This must return zero rows.** A non-zero row is a construction site that has not been given the new field."* It is retained because the failure is a clean instance of the thing its own correction block was invoking. **Measured against the very PR that added the command, it returns two rows, both false positives.** Both sites do carry the field.
>
> §15 step 6 was therefore **unsatisfiable as written**: the implementer had to either report a failed gate or clear the rows by reading and judging them — and clearing by judgement is exactly what this command was introduced to replace. The block cited #11228's *"prefer the representation whose truth is mechanically checkable"* and then shipped a representation whose stated truth condition is mechanically false.
>
> **Where the citation went wrong, since that is the reusable half.** #11228's rule is a **preference ordering, not a licence to declare decidability**. Replacing an unverifiable count with a no-false-negative screen was a real improvement and it stands; calling that screen a *gate* was the defect. The honest instrument labels its residual and asks the reader for five seconds, rather than asserting a predicate its own repository falsifies.
>
> **A third instance of the same failure was caught while writing this correction, and is recorded rather than quietly dropped.** The first draft of this fix prescribed the `rg --multiline-dotall` variant above as the remedy — reasoned, plausible, and **not run**. Running it showed it doubles the noise. That is CF-A's shape exactly (*prescribe a mechanism, never check it against the goal*) occurring inside the edit meant to fix CF-B, which is the strongest available evidence that this document's characteristic failure is **prescribing unexecuted mechanisms**, not any particular wrong answer. The standing rule that follows: **a command this document tells someone to run must have been run, and its actual output quoted.** Both (A) and (B) now satisfy that.
>
> **This is the same failure family as CF-A, one level up:** there, a number was quoted without the unit that gives it meaning; here, a command was quoted without the residual that bounds it. In both cases the artefact reads as more decisive than the evidence under it, and in both cases the fix is to state what the instrument does *not* settle.
>
> **The original W-11 lesson is unaffected and still load-bearing:** a sweep instrument is scoped to a vocabulary, and a design's ripples do not all share one. When a change alters a **signature** as well as a **contract**, it needs a sweep per axis. Ask, for each edit the design mandates, which command would find it — if the answer is "none", the instrument is incomplete, not the inventory.

**(C) The design self-check** — this document makes two kinds of claim about shipped code that no other instrument covers, and both have failed here:

**(C1) Every code fence quoting a user-facing string is a claim about shipped text.** Read each fence in §7 and §8.1 against the string it quotes. A fence is a **site of the change**, not a specification standing outside it — CF-F was a fence left showing a superseded message after the implementer changed it on this document's own instruction.

**(C2) Anticipatory tense**, which #114's Addendum 2026-08-27 says to grep for by name:

```
grep -nE "\b(should|needs to be|remains to be|will be|is to be)\b" \
  docs/architecture/divoid-mcp-credential-contract.md
```

**Ten rows at the time of writing, all of which clear** — eight outside this instrument block, plus two the block generates by quoting its own tells. A row clears only if it is one of these five, and the categories are exhaustive against the current text; **an unaccounted row is the finding**:

1. a genuinely open item in §16 (2 rows: the resolved item 1, the open item 2);
2. an instruction in §12's change inventory (1 row);
3. text inside a blockquote that is quoting something — a `> **CORRECTION**` block's withdrawn text, or Toni's verbatim ask in §1 (4 rows);
4. **shipped text that is legitimately future-tensed** — §8.1(a)'s deprecation `WARNING` says *"This fallback will be removed"*, and it will (1 row). Tense is a *tell*, not a verdict; a string whose subject is genuinely a future event is not stale;
5. **self-reference: rows inside this instrument block** (2 rows — the command itself, and category 4 above quoting the WARNING's tell). Guaranteed, and it moves whenever this section is edited, so **count the rows outside §12 and treat the in-section ones as fixed overhead** rather than re-deriving the total each round.

> A probe that greps a document containing the probe will always match itself, and a probe whose *clearing rules* quote the patterns they hunt will match those too. That is not a flaw to engineer around — it is the cost of keeping the tells written down where the next reader meets them, and it is cheap as long as the self-matches are declared instead of rediscovered.

Anything outside those five is prose written before a change and never revisited. **Future-imperative describing finished work is the exact tell**, and it is what CF-F's correction block had become.

> **Why (C) exists as its own instrument rather than as a wider scope on (A).** Widening (A) and the claim query to this file was necessary and is done, but it is not sufficient: those two are keyed to the **credential vocabulary**, and CF-F's defect was a *tense* and a *stale quotation*, which share no vocabulary with `divoid-online` or `fallback`. That is the original W-11 lesson — an instrument is scoped to a vocabulary — recurring on the artifact that states it. **The unlearned half, now learned twice: an instrument is scoped to a path as well as a vocabulary, and a design document needs coverage on both axes like any other file.**

### The standing rule for instruments in this document

1. **A command this document tells someone to run must have been run, with its actual output quoted.** (Added round 2, after three separate prescriptions of unexecuted mechanisms.)
2. **A command must be able to reach every artifact whose claims it asserts over — including this document.** State the paths it covers. (Added round 3, after CF-G.) Running a command proves what it does on the paths you gave it and says **nothing** about the paths you did not; rule 1 is fully satisfied by a correctly-executed, honestly-quoted, correctly-scoped-looking sweep of the wrong tree.
3. **A guarantee stated without its scope is not a guarantee.** *"No false negatives"* is a claim about a query **and** a path set; the path set is the half that gets dropped, because it is the half the author never had to think about.
4. **A vocabulary-keyed sweep cannot find a *consequence*.** When §5 changes, the sections stating what that change implies are read by a human — no instrument is built for them. (Added round 4, after W-1: §5.3's rationale was falsified in its *partial-export* half by §5.2's corrected predicate, and all four instruments miss it — (A) and the claim query share no vocabulary with it, (C2) does not carry `would`, and (C1) does not apply because it is prose rather than a fence.) **Resist building a fifth instrument for this**; the shape that fails here is inference, and a grep has no purchase on it.

Rules 1–3 compose into the check to run before believing any clean sweep: **did this command cover the file I am about to assert is clean?** For four rounds the answer here was no, and nothing surfaced it, because a clean result from the wrong paths is indistinguishable from a clean result that means something. Rule 4 is the boundary of that check — it marks where the instruments stop and reading starts, so a clean sweep is not mistaken for a complete one.

**Code**

| Site | Change |
|---|---|
| `src/divoid_mcp/config.py` | Rewrite: `ENV_URL` / `ENV_API_KEY` constants, `LEGACY_SECRET_FILE_PATH`, `_ENV_HINT`, `_from_env`, `_from_fallback_file`, the selector, the `source` field, the §7 messages, the §8.1(a) WARNING. Module docstring rewritten. |
| `src/divoid_mcp/errors.py:67` | 401 hint → §7.4 text. |
| `src/divoid_mcp/server.py:6` | Docstring step 2 no longer says "the DiVoid secret" is a file. |
| `src/divoid_mcp/server.py:71-76` | Extract `_build_instructions(config)`; append §8.1(b) sentence on the file source. |
| `tests/unit/test_config.py` | **New.** See §13. |
| `tests/unit/test_errors.py` | **New** *(added 2026-09-07, QA #13145 W-d)*. The §7.4 401-hint guards. Omitted from the original inventory. |
| `tests/unit/test_server.py` | **New** *(added 2026-09-07, QA #13145 W-d)*. The `_build_instructions` guards. Omitted from the original inventory. |
| `src/divoid_mcp/config.py` — message constants | `_MALFORMED_LINE_MSG` / `_EMPTY_VALUE_MSG`, module-level `Final[str]`, `.format()`ed at two branches each (§7.2). Named here because §12's `config.py` row did not mention them and the shape is the subject of CF-A. |
| `tests/smoke/run_all.py:7` | Docstring: credentials come from whatever `config.load_secret()` resolves. Behaviour unchanged — the call at `:82` still works on either source. |
| **Every `DivoidConfig(...)` construction site in `tests/unit/`** | Mechanical: add `source="env"`. Forced by §5.6 making `source` a third required field. Enumerate with re-derivation command **(B)**, not by counting here — the count is not a claim this document should make. `"env"` is the correct value at each site: `source` is consumed only by `_build_instructions`'s `startswith("file:")` test, none of these fixtures exercises it, and `"env"` keeps them on the non-deprecated branch. |

**Documentation**

| Site | Change |
|---|---|
| `README.md:48` | Prerequisite → the two environment variables, file named as deprecated fallback. |
| `README.md:52-59` | **Configuration** section rewritten: environment first, `env` block example, fallback below with its removal noted. |
| `README.md:96` | "Fail-closed auth" bullet → "no usable credential source" rather than "secret file". |
| `docs/install.md:37-54` | Step 2 rewritten to the `env` block; the file becomes a clearly-labelled alternative for existing installs. |
| `docs/install.md:110-116` | §3c generic-host list gains the two credential variables. |
| `docs/install.md:146` | Troubleshooting entry → the §7 F1 message. |
| `docs/install.md:157` | 401 entry → both sources. |
| `docs/install.md:70-74` | *(Added 2026-09-07.)* The verification step already uses `claude mcp list`; add one sentence recording **why** — `list` prints name, command and connect status only, while `claude mcp get` prints environment **values** in full plaintext (both measured, §9). Without it the step is safe for an unwritten reason, and the obvious "improvement" to `get` hands the reader a command that echoes their key. Cross-reference the existing `install.md:44` warning rather than restating it. |
| `examples/.mcp.json` | `env` block gains `DIVOID_MCP_URL` and `DIVOID_MCP_API_KEY` placeholders next to `DIVOID_MCP_LOG_LEVEL`. |
| `divoid-mcp/CLAUDE.md:19` | Invariant 1 restated per §9. |
| `divoid-mcp/CLAUDE.md:36` | Layout comment: `config.py # resolves credentials from the environment, fallback file; fail-closed`. |
| `divoid-mcp/docs/architecture/phase-1.md:33` | "Fail-closed auth" bullet updated; the node-#695 copy needs the same edit or an explicit note that this document supersedes that bullet. |
| `tests/smoke/README.md:9` | Prerequisite → either source. |
| `README.md:87` | *(Added 2026-09-07 — QA #13119 CF-5.)* The smoke-test paragraph asserts *"Requires `~/.claude/secrets/.divoid-online` with valid credentials"*, which this design falsifies and which contradicts `tests/smoke/README.md:9` in the same PR. → either source. **This row was missing from the original inventory**, and its absence is what let a sweep of command (A) clear the hit instead of patching it. |


#### Claim-keyed entry: the fallback's trigger condition *(added 2026-09-07, QA #13145 CF-C + the sixth site)*

**This entry is keyed to a claim, not to a site, and that is deliberate — the note at the end says why the site-keyed form failed twice.**

> **CLAIM (superseded):** *the deprecated fallback file is consulted when `DIVOID_MCP_API_KEY` is not set* — in any of its phrasings, including the conjunctive form *"environment incomplete **and** no fallback file"*.
>
> **Falsified by:** §5.2's corrected predicate. The fallback is consulted only when **neither** variable is set, and an incomplete environment fails closed **regardless** of whether the file exists.
>
> **Replacement wording:** the symmetric trigger — *"when neither `DIVOID_MCP_URL` nor `DIVOID_MCP_API_KEY` is set"* — plus, where the text describes the failure, *"the file is not consulted even if it exists"*.

**Find the sites with this, rather than trusting the list under it.** Key on the claim's **subject**, not on its wording:

```
grep -rni "fallback" divoid-mcp/README.md divoid-mcp/docs/ 
  docs/architecture/divoid-mcp-credential-contract.md --include=*.md
```

**Read every row.** The fallback is the subject of the claim, so any statement of its trigger must name it — **no false negatives within the paths given**, which is the whole of the guarantee and the reason the paths are now written out. **64 rows** at the time of writing: **12** across `divoid-mcp/`, containing all five prose sites in the table below, and **52** from this document.

**Most of this document's rows clear by construction, and the clearing rule is mechanical rather than a judgement call:** superseded text retained inside a `> **CORRECTION**` block is *supposed* to state the false claim — that is the retraction discipline (#11228 Lesson 3) working, not a stale site. So: **a hit inside a correction blockquote clears; a hit anywhere else must be re-derived from the shipped code.** Worth naming as a standing tension — the retraction discipline **guarantees** this document contains verbatim copies of every claim the sweep hunts, so the two conventions would fight each other without that rule.

> **The first version of this query keyed on wording and was wrong:** `"is not set|incomplete|falls? back"`. It misses `install.md:154`, which phrases the same claim as *"neither … nor the fallback file **is set up**"*. That is #11228 **Lesson 1** exactly — *"grep the claim, not the wording you are deleting"* — committed inside the entry written to apply #11228, and caught only by testing the pattern against the sites it was supposed to find. Recorded because the pull toward the wording evidently survives reading the rule minutes earlier: **the words in front of you when you write the query are the words of the version you are removing, and those are the one set of words the next site is least likely to share.**

**Sites known at the time of writing — six:**

| Site | Form the claim took |
|---|---|
| `divoid-mcp/README.md:66` | Key-only trigger. Also gains a pointer to `docs/install.md`'s Troubleshooting section, the README having none of its own. |
| `divoid-mcp/README.md:105` | The *"environment incomplete **and** no fallback file"* conjunction. |
| `divoid-mcp/docs/install.md:50` | Key-only trigger, in the deprecated-fallback section an existing install is sent to. Also gains the *"not consulted even if it exists"* note. |
| `divoid-mcp/docs/install.md:154` | The F1 troubleshooting bullet, naming only `DIVOID_MCP_API_KEY`; now names both. |
| `divoid-mcp/docs/architecture/phase-1.md:33` | The same conjunction as `README.md:105`. **Not cited by review** — found by the implementer re-running sweep (A) and treating its clear result as a claim he was making rather than a box already ticked. |
| `divoid-mcp/docs/install.md`, Troubleshooting | **New E2 bullet** beside the E1 one: the E2 message, the wrong-host reason the file is not consulted, and the misspelled-variable cause. E2 is the failure mode CF-9 introduced and the one a half-migrated operator actually hits. |

> **Why this entry is claim-keyed, and the rule it generalises to.** The site-keyed form of this inventory shipped **twice** with a hole in it, and the second time the hole was a site carrying a defect *identical* to one the review had already cited. Review caught `README.md:105`; `phase-1.md:33` said the same false thing and was found only because someone re-ran the sweep and refused to treat a previous clear as settled.
>
> A row per site is an **enumeration**, and this section's own preamble says an enumeration *"may list, but never assert coverage"* — yet a site-keyed table gets read as coverage anyway, because there is nothing else in the row to check against. A claim-keyed entry inverts that: it states the false proposition, gives a query that finds instances of it, and demotes the site list to *what was known when this was written*. **The second site becomes findable without depending on a reviewer having noticed the first.**
>
> **This is CF-A's failure one level up, and the resemblance is not a coincidence.** CF-A was a *number quoted without the unit that makes it mean anything*; this is a *site listed without the claim that makes its siblings findable*. Both record an instance while omitting the thing that generalises from it, and both then read as more complete than they are. The remedy is identical in both places — state the thing the instance is an instance **of**: a `Unit` column in §14's table, a `CLAIM` line here.
>
> **The standing rule for this document:** a decision that changes *what the software does to a user* generates its documentation entry in the same edit that changes the decision, and that entry is keyed to the claim it falsifies. An absent entry does not produce a silent omission — it produces a confident **wrong clearance**, because a sweeper with no row to compare against reads the hit as pre-existing prose the diff never touched. #114's 2026-08-28 addendum ranks that as the worse of the two failures. Concretely: **when §5 changes, walk §12 before writing anything else.**

**Deliberately not changed** (each mentions the path but remains true, because the file itself survives): `docs/drift-policy.md:35-36`, `docs/architecture/phase-1.md:10-11`, `tests/smoke/README.md:89-90` — `awk`-based curl recipes that read a file which survives Phase 2 (§4). `README.md:65` (the `DIVOID_MCP_FILE_ROOT` paragraph) and `tests/unit/test_paths.py:106` — both use the path as the *exfiltration target* the containment gate protects, which is unaffected.

**A hit being on this list is not a licence to skip it.** Re-derive each from the post-diff source before clearing it; the list records a judgement made at design time against `7700ebb`, and the diff is what could have invalidated it.

## 13. Coverage — named guards

**These tests do not exist yet; they are deliverables of this design.** Do not attempt to resolve the names against `7700ebb` — `tests/unit/` contains no `test_config.py` at that ref. All are hermetic (no network, no live DiVoid), using `monkeypatch` and `tmp_path` in the style of `tests/unit/test_paths.py`.

**No falsifier has been executed for any row below.** This document's author has no harness and quotes no observed red run, so per the falsifier discipline these rows carry a *discriminating premise* — the reason the named test cannot pass against an implementation lacking the property — and not a claimed measurement. Establishing the red runs is part of the implementation.

| Property | Named guard | Why it discriminates |
|---|---|---|
| Environment wins over the file | `test_env_source_wins_over_fallback_file` | Both sources present, holding **different** base URLs; the assertion is on the returned `base_url`. A file-preferring or merging loader returns the other URL. |
| A source is used whole, direction (i) — §5.2 | `test_env_api_key_without_url_exits_without_reading_the_file` | `DIVOID_MCP_API_KEY` set, `DIVOID_MCP_URL` absent, **a valid file present** at the injected path. Asserts `SystemExit(1)`. A fall-through implementation succeeds instead of exiting — the presence of the valid file is what makes the exit meaningful. |
| **A source is used whole, direction (ii) — §5.2** *(added 2026-09-07, CF-9)* | `test_env_url_without_api_key_exits_without_reading_the_file` | `DIVOID_MCP_URL` set to a **distinctive second-instance URL**, `DIVOID_MCP_API_KEY` absent, **a valid file present** holding a *different* URL and a key. Asserts `SystemExit(1)`. This is the guard for the defect QA demonstrated: the shipped key-only selector returns the file's config here, so the test is red against it and green only against §5.2's corrected predicate. The distinct URLs are load-bearing — if both sources carried the same URL the test would pass against a merging loader too. |
| E2 names the ignored variable and the non-consultation | `test_env_url_without_api_key_message_names_the_key_and_the_file` | Same setup; asserts the captured log text contains `DIVOID_MCP_API_KEY`, the resolved fallback path, and the token `NOT consulted`. **The `_ENV_HINT` trap applies here** — the hint names both variables on every branch, so an assertion on `DIVOID_MCP_API_KEY` alone has zero discriminating power (QA #13119 CF-4). The path and the `NOT consulted` token are what make this guard constrain E2's own body; emptying that body to `"boom"` must turn it red. |
| Empty/whitespace `DIVOID_MCP_URL` still falls back (§5.4) | `test_empty_env_url_falls_back_to_file` | `DIVOID_MCP_URL=""` (and a whitespace-only parameterisation), no key, valid file. Asserts the file's config is returned and **no** `SystemExit`. This is the negative direction of the row above, and it is not padding: without it, a selector that tested membership rather than non-emptiness would pass every other row while breaking every existing `env: {}` install that some host populated with empty strings. |
| The fallback still works untouched | `test_falls_back_to_file_when_api_key_absent` | Empty environment, valid file; asserts the returned config and `source == f"file:{path}"`. This is the zero-migration claim in the TL;DR. |
| Empty string counts as absent (§5.4) | `test_empty_env_api_key_falls_back_to_file` | `DIVOID_MCP_API_KEY=""` plus a valid file. A membership-based selector (`"…" in env`) takes the env branch and exits 1 instead of returning the file's config. |
| Values are stripped (§5.4) | `test_env_values_are_stripped` | Both variables set with a trailing newline; asserts the stored values have none. A no-strip implementation stores the newline and the test fails on exact comparison. |
| The no-credentials refusal teaches the contract | `test_no_credentials_message_names_both_variables_and_the_path` | Empty environment, no file; asserts exit code 1 **and** that the captured log text contains `DIVOID_MCP_URL`, `DIVOID_MCP_API_KEY` and the resolved path. Today's message (`config.py:42`) names only the path and fails this. |
| Every failure branch teaches it, and each names its own line | `test_malformed_file_missing_url_line_names_url`, `test_malformed_file_missing_apikey_line_names_apikey`, `test_empty_value_file_url_empty_names_url`, `test_empty_value_file_apikey_empty_names_apikey` | Four tests, one per parameterisation of F4 and F5. Each asserts the message names **its own** `Url=` / `ApiKey=` line **and** — the half that carries the weight — that it does **not** name the other. The negative assertion is what makes the `prefix` / `key` argument a guarded axis rather than a silently-constant one (#114 §13.1.1): without it, reporting a missing `ApiKey=` as a missing `Url=` passes. |

> **CORRECTION 2026-09-07 (QA #13145, W-c).** This row previously named a single guard, `test_malformed_file_message_names_the_env_variables`, asserting only that the message contains `DIVOID_MCP_API_KEY`. **No such test exists in the tree.** It was superseded during implementation by the four tests above — a strictly better shape, since the original's sole assertion is satisfied by `_ENV_HINT` on every branch and therefore constrained nothing (that is the trap this table documents two rows below). Retained because the failure is the one this document keeps making in a new place: **§13 was resynced against the round-1 corrections but never against what the implementation actually shipped**, leaving a table that names one guard that does not exist and omits eight that do. A coverage table is a claims-bearing artefact like any other; reconciling it against the tree is part of closing a round, not an optional tidy-up.
| **Each message body is discriminated by its own guard** *(added 2026-09-07, CF-4)* | one assertion per template, inside the tests already named above | Each of E1, E2, F1, F4, F5 must have at least one assertion on a token that appears **only in that template** — not one that `_ENV_HINT` supplies. The falsifier is stated as a property rather than a case list: **replace any single message body with the literal `"boom"`, keeping `_fail`'s hint append intact, and at least one test must go red.** Cover both parameterisations of F4 and F5 (`Url=` and `ApiKey=`); the `prefix` / `key` argument is an independent axis, and an axis the mutations never varied reports zero survivors, which reads exactly like coverage (#114 §13.1.1). |
| The 401 hint is source-agnostic (§7.4) *(added 2026-09-07, CF-3)* | `test_401_hint_names_the_env_variable_and_no_hardcoded_path` | Maps a synthetic 401 through `errors.map_http_error`; asserts the produced text contains `DIVOID_MCP_API_KEY` and the `"Config loaded: source="` pointer, and **does not** contain the literal `.divoid-online`. The negative half is the point: the pre-change hint named that path unconditionally, and it is what makes a regression to a hardcoded source visible. |
| Fail-closed is preserved | `test_every_failure_branch_exits_nonzero` | Parameterised over absent / unreadable / empty / missing-line / empty-value / env-without-url / **env-without-api-key**; each asserts `SystemExit` with code 1. An implementation that returns a partial config or raises instead of exiting fails. **The seventh parameterisation is required by the test's own name** — E2 is a failure branch, the property is guarded elsewhere, but this enumeration is where an eighth branch would be noticed, and a name asserting completeness must be complete (QA #13145, W-b). |
| The default environment is read (W-1) | `test_env_none_defaults_to_os_environ` | Calls `load_secret()` with no `env=`; asserts the value from the patched `os.environ` is used. `config.py`'s `env = os.environ` line is the only branch production takes and every other test injects `env=`, so without this row it is executed by nothing. |
| The 401 hint's remaining surface | `test_401_prefixes_context_when_given`, `test_401_omits_context_prefix_when_not_given`, `test_401_never_echoes_the_api_key` | Three guards beside the §7.4 row above, in `tests/unit/test_errors.py`. The last is the containment guard on the error path: invariant 1 is asserted where a key could most plausibly re-enter user-facing text. |
| The key is never logged | `test_api_key_never_appears_in_any_log_record` | Distinctive sentinel key via the environment; asserts the sentinel appears in **no** emitted record. `DivoidConfig` is a dataclass whose `repr` contains the key, so a `logger.info("Config loaded: %s", config)` would leak — this test is what makes that a red. |
| `source` carries no secret | `test_source_field_never_contains_the_api_key` | Runs both branches with a sentinel key; asserts the sentinel is not a substring of `config.source`. |
| Deprecation is visible on the file branch only | `test_deprecation_warning_only_on_file_source` | Two runs; asserts a `WARNING` record containing "DEPRECATED credential source" on the file run and **no** such record on the env run. An unconditional warning fails the second half. |
| The Phase-2 observable exists and is conditional | `test_instructions_carry_deprecation_sentence_only_on_file_source` | Calls `server._build_instructions(config)` with each `source`; asserts the §8.1(b) sentence present on `file:…` and absent on `"env"`. **The presence assertion must pin a distinctive multi-word span of that sentence, not the token `DEPRECATED`** — §8.1(b) says the wording is load-bearing, and a single-token assertion is satisfied by any string containing the word, so a reword that breaks the Phase-2 check stays green (QA #13119 W-3). Falsifier: reword the sentence while keeping the token, and this must go red. |
| The base `instructions` survive the extraction | `test_instructions_contain_the_base_description_on_both_sources` | `_build_instructions` was extracted from an inline string **specifically** to make it testable; the non-conditional half is currently unguarded, so emptying the base body leaves the suite green (QA #13119 W-3, M25). Asserts a distinctive span of the base text is present regardless of `source`. |

Three coverage notes stated as limits rather than left implicit:

- **The shared hint is a guard-set trap, and it is this design's own creation.** §6.3 extracts `_ENV_HINT` and §7.1 appends it to *every* branch — which means any assertion of the form *"the message names `DIVOID_MCP_URL` / `DIVOID_MCP_API_KEY`"* is satisfied on **every** branch by the hint alone, no matter which branch the test is named for. A guard that constrains nothing is indistinguishable from one that does, because both are green (#114 §13.1.1). The DRY win in §6.3 is real and stands; this is its cost, and it is paid by requiring a per-template discriminating assertion (the row above) rather than by un-extracting the hint.
- **`README.md` / `docs/install.md` prose is not pinned by any test.** Documentation drift between the code's variable names and the docs is caught only by review. A test asserting the README contains `ENV_API_KEY` would be a grep — an instrument that tests spellings, and one that would pass against a README whose surrounding prose is wrong. It is not worth the false confidence.
- **The Phase-2 trigger (§8.2) is a human check, not a test.** Nothing in CI can observe another operator's machine.

## 14. Pre-Design Checklist (#1136 §5)

**KISS / DRY / YAGNI**

- No new type mirroring an existing one. `DivoidConfig` gains one field; no parallel config object.
- No new abstraction with one implementation. `_from_env` / `_from_fallback_file` are two private functions inside one module, not an interface with a strategy.
- No element justified by "we might need X later": `DIVOID_MCP_CREDENTIALS_FILE` (§5.1), `server.json` (§6.1), a `--check-config` flag (§6.2), URL validation (§4), an `os.environ.pop` (§9) and any migration telemetry (§8.2) were each considered and cut.
- The deprecation window is a real one — the population is other people's already-installed machines, not a monorepo with atomic deploy — so §1136's ban on transition windows does not apply, and §8.2 names the observable that ends it rather than a date.
- DRY math, quoted — **every** extract/inline decision this design takes, per #1136 §5:

  | Decision | **Unit — what the number decides** | `block_size × site_count` (#1267 ≈ 15–20) | Call |
  |---|---|---|---|
  | The credential-hint block (§6.3, §7.1) | whether a repeated **block** becomes a symbol | 10 rendered lines × 8 sites (E1, E2, F1–F5, §8.1(a)) = **80**, far above | **Extract** to `_ENV_HINT` |
  | A private `_malformed_file` / `_empty_value_file` **helper function** (§7.2) | whether a **function** earns its keep — **not** where the text lives | ~3 lines × 2 sites = **6**, below | **No helper.** The text still lives exactly once, as a module-level `Final[str]` template (§7.2). This number decides the function and nothing else. |
  | Folding E1 and E2 into one parameterised **message** (§7.2) | whether two **distinct diagnoses** are really one message with an argument | the genuinely shared span is ~1.5 rendered lines × 2 sites = **3**, below | **Two texts.** The unit is the message, not the line. |

  **The unit is what makes the number mean anything, which is why it is now a column.** A span quoted without its unit answers whichever question the reader brings — and quoting one produced two consecutive wrong answers on the same four lines of F4/F5 (diagnosed in §7.2's second correction block). Where the unit is *the message*, line-count math is not the deciding instrument at all and the row says so.

  > **CORRECTION 2026-09-07 (QA #13145, W-i).** The E1/E2 row previously read *"~4 lines × 2 sites = **8**"*. The verdict was right and the measurement was not: it counted the **whole message** at both sites when only about a sentence is actually common to the two. Retained because the error is instructive in the same direction as CF-A — an over-counted span still landed below threshold and so produced the correct call by luck, which is precisely the condition under which a wrong measurement survives review. Note also, for whoever edits either message next: **E1 and E2 do share one verbatim sentence across two literals, and the copies already differ in punctuation.** That is accepted, not overlooked — extracting at a span of 3 would be CF-6 in reverse.

  > **CORRECTION 2026-09-07 (QA review #13119, CF-6).** This bullet previously ended with the sentence *"No inline-at-N-sites decision is taken anywhere in this design."* It was **false, and self-contradicted by the `_ENV_HINT` math in the same sentence**. It is retained here because it is the sentence an implementer would cite to skip the math on §7.2's helpers, and QA found that is exactly what happened: `_malformed_file` and `_empty_value_file` shipped as four-line helpers with two call sites each and no math anywhere. The table above replaces the claim with the enumeration it should always have been. **The general form:** a checklist answer of the shape *"no such decision is taken"* is a coverage assertion, and #1136 §5 asks for the math, not for the absence of the question. If the honest answer is "none", the enumeration is empty and costs one line; if it is not empty, the negative claim hides it.

**Existing systems first**

- The environment-reading pattern already exists twice in this process (`paths.py:14`, `server.py:36`) and is reused rather than reinvented, down to the injectable `env=` parameter (§5.5).
- No new layer, module, service or file is introduced except the test module. `server.json` was the one candidate new file and was cut in §6.1 on the consumer-chain rule.
- The one new persisted-ish data point, `DivoidConfig.source`, enables two named consumers within this PR (§5.6, §8.1(b)) — not a 4-week hypothetical.

**Configurability**

- Two new environment variables. Both qualify under §1136 §3's third clause: the value is a secret/endpoint that cannot live in code. Neither is a tuning knob.
- No default is invented for `DIVOID_MCP_URL`. A baked-in `https://divoid.mamgo.io/api` would silently point a stranger's server at this deployment — `divoid-mcp/CLAUDE.md:15` records that this is a generic tool used outside it.

**Less is better**

- Delete/merge/inline was run on every element; the six cuts are listed above. Two mechanisms were **merged**: the deprecation-visibility channel and the Phase-2 migration observable are one string (§8.1(b), §8.2), rather than a nag plus a telemetry counter.
- Trade-offs named explicitly: whole-source selection over merging **in both directions** (§5.2), failing closed over warning-and-continuing on a half-set environment (§5.2), stating the environment exposure over defending against it (§9), a conditional `instructions` sentence over a stderr-only warning (§8.1).

  > **CORRECTION 2026-09-07 (QA review #13119, CF-9).** This bullet previously read *"whole-source selection over merging (§5.2)"* without qualification, which asserted a completeness the rule as written did not have — the rule was closed against direction (i) and open against direction (ii). Retained because the shape generalises: **naming a trade-off in a checklist does not verify it**, and a one-word entry pointing at a section is the least likely place a reader will notice that the section only does half of what its title says. Where a rule has directions, the checklist entry names them.

**Document discipline**

- Cites **Code Contracts #114** and **Design Contracts #1136** as load-bearing, plus `divoid-mcp/CLAUDE.md` (invariants 1, 2, 6), #6108 §1 and §4, #5978, #695.

  **#114 governs this repository, including its Python code.** The repo's own `CLAUDE.md` names #114 as the backend code contract for the monorepo — and that monorepo explicitly includes `divoid-mcp/` — and no contract-deviation node exists for `divoid-mcp`. What is true is narrower and is stated here so an implementer does not have to guess the boundary:

  | #114 content | Binds this PR? |
  |---|---|
  | §0 (KISS/DRY/YAGNI + the bounce rule), §4 (comments — where rationale lives and that it must still be **true**), §13.1.1 (a guard set constrains only where its mutations varied), §16 (pre-PR checklist), the stale-claim addenda of 2026-08-27 / 08-28 / 08-29 | **Yes, directly.** These are language-independent. §4's channel analogue in Python is the docstring for `<summary>`; there is no analogue for a body `//`, because the rule is that there are none. |
  | Ocelot idioms, `[AllowPatch]`, `[PrimaryKey]`/`[Index]` entity attributes, `WebApplicationFactory`, `var`-vs-explicit-types, XML `<remarks>`, one-type-per-file | **No.** These are C#/Ocelot **mechanics** with no Python construct to bind. |

  > **CORRECTION 2026-09-07 (QA review #13119, CF-7).** This bullet previously read *"#114 is a C#/Ocelot contract and does not bind Python code; its §0 principles are subsumed by #1136 §1 here."* The first clause is **false** and the sentence is retained because it did damage rather than merely being wrong: it sat inside the box meant to satisfy #1136 §5's *"Design doc cites Code Contracts (#114) **and** Design Contracts as load-bearing"* item, it is the sentence an implementer would cite to skip §4, and QA traced three findings (a stale invariant left in a module docstring, two helper docstrings restating their own bodies, and an uncited multi-line platform-quirk comment) to exactly that skip. A design may say which *mechanics* of a contract have no analogue in its language; it may not decide that the contract does not apply. **Scoping a contract is a deviation, and a deviation is a graph node someone else agrees to — never a clause inside the document that benefits from it.**
- Scope and non-scope are both explicit (§4).
- No predecessor design is superseded end-to-end. `phase-1.md:33` and node #695 carry one bullet that this document narrows; §12 requires that bullet to be updated in the same PR rather than left reading as current.

## 15. Implementation order

1. **`config.py`** — constants, `_ENV_HINT`, `_from_env`, `_from_fallback_file`, the **§5.2 selector predicate** (either variable non-empty selects the environment), `source`, the §7 messages **including E2**, the §8.1(a) warning, the §7.3 startup line. `_malformed_file` / `_empty_value_file` helper **functions** are not introduced; F4's and F5's wording lives in one module-level `Final[str]` template each, `.format()`ed at both of its branches, per §7.2's shape table. Nothing else compiles against it, so it lands first and alone.
2. **`tests/unit/test_config.py`** — the §13 rows, minus the `_build_instructions` and `errors.py` rows. Run the suite; every row must be red before the corresponding behaviour exists and green after.
3. **`server.py`** — extract `_build_instructions(config)`, append the §8.1(b) sentence conditionally, fix the `:6` docstring. Add the two `_build_instructions` §13 rows.
4. **`errors.py:67`** — the §7.4 hint, **plus its §13 guard row**. The hint is user-facing changed behaviour; it does not ship without a test.
5. **Documentation** — every row in §12's documentation table, plus `examples/.mcp.json`, in one pass.
6. **Run both §12 re-derivation commands.** (A) must leave every surviving hit on the deliberately-not-changed list, **and each cleared hit re-derived by reading the post-diff line** rather than by recalling the edit — a wrong clearance is worse than a missed one (#114, 2026-08-28). For (B), **read every row it returns and clear each against the source** — it is a no-false-negative screen, not a zero-row gate, and rows are expected (§12). Report both outputs, including the zeros and every cleared row.
7. **The §4 sweep of the touched regions.** Every docstring and comment the diff introduces or modifies must still be **true** after the diff, not merely short (#114 §4 + Addendum 2026-08-27). Specifically: `config.py`'s module docstring states key containment, which §9 declares incomplete under an environment source — the invariant belongs in the design doc and `divoid-mcp/CLAUDE.md`, not in a module docstring, so remove it there rather than restating it in two places.
8. **Manual verification on this machine**, in this order: (a) environment unset, file present → server starts, `source=file:…`, WARNING present, instructions carry the sentence; (b) both variables set in `~/.claude.json`'s `env` block → `source=env`, no WARNING, no sentence; (c) `DIVOID_MCP_URL` unset, key set → exit 1 with E1; (d) **`DIVOID_MCP_API_KEY` unset, `DIVOID_MCP_URL` set, file present and valid → exit 1 with E2, and the file's credentials are not used**; (e) both unset and the file moved aside → exit 1 with F1. Restore the file. Report the five observed outcomes. Step (d) is the CF-9 acceptance check and is the one that was previously absent.
9. **File the Phase-2 task** on DiVoid, linked to this document and to #13105, holding the roster and quoting §8.2's observable.
10. **Reconcile the graph half of the sweep.** DiVoid **#695** still carries the pre-contract wording *"Auth key is read from `~/.claude/secrets/.divoid-online` at startup"*, which this design falsifies. A note in `phase-1.md` acknowledging it is a **deferral, not a patch** — a reader landing on #695 directly sees nothing. The repo half of a stale-claim sweep has a forcing function and the graph half has none, which is why it is the half that gets skipped (#114, 2026-08-29); it is listed as its own numbered step for that reason.

Steps 1–7 are one PR. Steps 9 and 10 are graph writes, not code changes, and belong to the orchestrator.

## 16. Open items

Stated here rather than left implicit, because each is a thing this document does **not** know:

| # | Open item | Who resolves it | Why it is not closed here |
|---|---|---|---|
| 1 | ~~Does `claude mcp list` print environment **values**?~~ **RESOLVED 2026-09-07: no.** | Closed by the orchestrator's dummy-sentinel probe; result and re-runnable form in §9 | Kept in this table rather than deleted, because the answer is the useful artefact: **`list` verifies, `get` discloses.** `docs/install.md:70-74` therefore uses the safe command of the pair, and now says so on the record. Anyone tempted to switch that step to `claude mcp get` for its richer output should read §9 first. |
| 2 | **PARTLY ANSWERED 2026-09-07.** Does any operator on the roster have `DIVOID_MCP_URL` exported from a shell profile? | **This machine: no, measured** (§5.3's enumeration). **Paul's machine and anyone who followed `install.md`: unknown** — orchestrator, by asking the roster | §5.2 turns that state from "silently ignored" into "refuses to start". The design's position is that this state is a mistake and should be reported, and the population is enumerable (*"currently its just our team"*), so this is a question to ask rather than a risk to model. **The measured half also cost nothing to obtain and settled a second question** — it is what turned §5.3's prefix rationale from asserted into measured, and revealed that the unprefixed alternative would have refused to boot on this machine. The remaining half is the last thing standing between this design and an operator surprise. |
| 3 | Phase 2's roster | The Phase-2 task (step 9) | Roster membership changes as people are added; §8.2 states the observable, the task holds the checkboxes. |
