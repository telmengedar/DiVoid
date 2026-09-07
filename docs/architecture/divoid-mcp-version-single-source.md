# Architectural Document: divoid-mcp — a single source for the package version

> Repo path: `docs/architecture/divoid-mcp-version-single-source.md` · DiVoid node: see §12.
> Task: DiVoid **#13109**. Concept it amends: **#6108** §4 (pinned-install contract).
> Base: `main` @ `df3436b` ("Merge pull request #186 …"). Every `file:line` below resolved against that ref.

## TL;DR

**What:** delete the version literal from `pyproject.toml`; the build reads the one in `version.py`,
which is otherwise untouched — `__version__` and `PINNED_API_REF_HASH` both stay.

**How:** `pyproject.toml:7` becomes `dynamic = ["version"]` plus a `[tool.hatch.version]` table
pointing at `src/divoid_mcp/version.py`. Hatchling is already the backend and reads it by regex — no
new dependency, no build-time import. Measured: builds `divoid_mcp-0.11.0-py3-none-any.whl`, and a
clean install reports `0.11.0` from both `pip show` and `__version__`.

**Cost:** three lines of TOML, one ~20-line unit test, one line of `CLAUDE.md`. No runtime code
changes; resolved version becomes `0.11.0`, no renumber.

**Bounded to** built artifacts and non-editable installs (#6108 §4's operational path); an editable
dev venv re-skews after a later `__version__` bump until reinstalled — measured by QA #13173, and
true of every option in §5.

**Rejected:** hand re-syncing the number — prescribed three times (#1434, #1638, #11369), drifted
three times. Full alternatives in §5.

---

## 1. Problem statement

The brief, verbatim:

> "decide how `divoid-mcp` carries its version, so the packaged version and the running version
> cannot disagree."

and on what would *not* count as an answer:

> "this has already drifted once, and a fix that re-syncs today while leaving the mechanism intact
> has not addressed the task."

One fact — "which version is this package" — is written down twice, by hand, in two files. The
copies have disagreed since 2026-09-03. Under **#6108 §4** the operational server must be installed
pinned and non-editable; that contract is only enforceable if the version a `pip install` resolves is
the version the running process reports. Today it is not, and the skew is agent-visible: the server's
MCP `instructions` string is built from `__version__` at `server.py:67`.

**The premise is confirmed, and it is worse than "drifted once".** See §2.

## 2. What I ran

Every claim below is an observed output, not a prediction. Commands were run against
`C:\dev\claude\divoid` at `df3436b`, and against throwaway copies of `divoid-mcp/` under
`C:\dev\claude\_scratch\` (`.venv`, `__pycache__` and `.pytest_cache` excluded from every copy).

### 2.1 The two literals

```
$ cat -n divoid-mcp/pyproject.toml            ->  7  version = "0.10.0"
$ cat -n divoid-mcp/src/divoid_mcp/version.py -> 16  __version__ = "0.11.0"
```

Confirmed. These are the **only** two version literals in the subtree. A sweep of `src/`, `tests/`,
`docs/`, `README.md`, `CLAUDE.md` and `examples/` (with `.venv` excluded) found one further mention,
`docs/install.md:27` — `"Successfully installed divoid-mcp-0.1.0 (or a similar version number)"` —
which is illustrative sample output carrying its own hedge, not a third source of truth. **No change
proposed there**, stated so the next reader does not re-discover it as an omission.

### 2.2 The drift is a single-commit event, and it has happened three times

`git log -L` on each line shows the two files moving in lockstep for six consecutive bumps
(0.5.0 → 0.10.0), then one commit that moved only one of them:

```
$ git show --stat --oneline 2647064
2647064 fix(divoid-mcp): line edits no longer swallow the line after the range (DiVoid #11011)
 divoid-mcp/src/divoid_mcp/tools/edit_content.py |  53 +++-
 divoid-mcp/src/divoid_mcp/tools/get_content.py  |  11 +-
 divoid-mcp/src/divoid_mcp/version.py            |   2 +-
 divoid-mcp/tests/unit/test_edit_content.py      | 364 ++++++++++++++++++++++++

$ git show --name-only --format= 2647064 | grep -c pyproject   ->  0
```

2026-09-03. `version.py` 0.10.0 → 0.11.0; `pyproject.toml` untouched.

**And the graph already holds three prior filings of the same defect, all still open:**

| Node | Date | Pair | What it prescribed |
|---|---|---|---|
| **#1434** | 2026-05-30 | 0.2.0 / 0.3.0 | "Single-line fix: update `pyproject.toml` … fold into the next PR" |
| **#1638** | 2026-06-04 | 0.2.0 / 0.4.0 | "Pick one of two patterns, apply once, never drift again" — recommended dynamic-from-`version.py` |
| **#11369** | 2026-09-05 | 0.10.0 / 0.11.0 | "Set `pyproject.toml` to `0.11.0`"; recorded single-sourcing as an undecided follow-up |

**#11369 and #13109 are the same defect at the same version pair** — #13109 is a duplicate, filed
independently. That is a finding for the operator (§11), not something I act on.

This is the load-bearing measurement for the whole design: **manual re-sync is not an untried
option. It is the option that has been prescribed three times and has failed three times.** #1638
already chose the mechanism this document chooses, in June, and it was never implemented.

### 2.3 Both prior tasks name a recipe that does not work in this repo

#1638 and #11369 both prescribe `[tool.setuptools.dynamic]`. The build backend here is **hatchling**
(`pyproject.toml:2-3`). Applied verbatim to a copy of the tree:

```
$ python -m pip wheel --no-deps -w dist .
  Preparing metadata (pyproject.toml): finished with status 'error'
    File ".../hatchling/metadata/core.py", line 249, in _get_version
      version = self.hatch.version.cached
error: metadata-generation-failed
```

The failure is loud, not silent — but an implementer following either node verbatim loses a round.
Recorded here so the correct directive travels with the design.

### 2.4 The skew survives packaging — this is not a working-tree artifact

Building the **unmodified** tree today:

```
Created wheel for divoid-mcp: filename=divoid_mcp-0.10.0-py3-none-any.whl
```

…while `divoid_mcp/version.py` inside that same wheel says `0.11.0`.

And on the **operational, non-editable** install on this machine
(`C:\Users\max_g\AppData\Roaming\Python\Python314\site-packages`, no `.pth`, i.e. a real install):

```
module file       : ...\site-packages\divoid_mcp\__init__.py
module __version__: 0.11.0
metadata version  : 0.10.0
```

One installed artifact, two answers. `pip show divoid-mcp` — the only check available before the
package is imported — reports `0.10.0`.

### 2.5 The proposed mechanism, built and installed

Patch applied to a copy: `pyproject.toml:7` → `dynamic = ["version"]`, plus
`[tool.hatch.version] path = "src/divoid_mcp/version.py"`.

```
Created wheel for divoid-mcp: filename=divoid_mcp-0.11.0-py3-none-any.whl
```

Installed non-editable into a clean venv:

```
pip metadata   : 0.11.0
__version__    : 0.11.0
AGREE          : True
```

### 2.6 Hatchling text-parses `version.py`; it does not import it

This determines whether the drift pin may keep sharing the file. I poisoned the copy's `version.py`
with `import httpx` — a module absent from the isolated build environment — and rebuilt:

```
Created wheel for divoid-mcp: filename=divoid_mcp-0.11.0-py3-none-any.whl
```

The build did not care. Hatchling's default version source is a regex over the file's text, so
**`version.py`'s other contents are invisible to the build** and no constraint is imposed on them.

### 2.7 Re-introducing a second literal is a hard build error

```
ValueError: Metadata field `version` cannot be both statically defined and listed in
field `project.dynamic`
```

This is PEP 621, enforced by the backend. See §7 — it is half the guard, and it is free.

### 2.8 There is no CI

```
$ git ls-files | grep -iE "\.github/|\.gitlab-ci|azure-pipelines|Jenkinsfile|\.circleci"
(none tracked)
```

`.github/workflows/frontend.yml` exists on disk but is **untracked**. So a check that only runs in CI
would never run. The guard must live in `tests/unit/`, which is what people actually execute.

### 2.9 No existing test observes this

```
$ grep -rn "pyproject\|tomllib\|toml" tests/ --include=*.py
(none — no test reads pyproject.toml at all)
```

And `tomllib` is stdlib from 3.11; `requires-python = ">=3.11"` (`pyproject.toml:11`). The guard
needs no new dependency.

## 3. Scope

**In scope**

- Where the package version literal lives, and how the build obtains it.
- Whether `PINNED_API_REF_HASH` continues to share `version.py`.
- The guard against recurrence, and the naming of guards that cannot fail.

**Out of scope**

- Choosing a *new* version number, or any release/tagging process. §5.4 settles which number results.
- `PINNED_API_REF_HASH`'s **value** and its staleness (#1638's caveat, #6108 §3). Untouched here.
- The credential contract (#13105 / #13108), the drift canary's behaviour, and every other coupling
  in #6108.
- Closing or correcting #1434 / #1638 / #11369. Recommended in §11; the operator owns task hygiene.
- Publishing to an index. Installs are `pip install git+…` (`docs/install.md:24`); there is no index,
  so the version is a reporting and upgrade-comparison label, not a resolvable index key.

## 4. Decision

**`src/divoid_mcp/version.py` is the single source. `pyproject.toml` derives from it.**

```
[project]
dynamic = ["version"]          # replaces  version = "0.10.0"

[tool.hatch.version]
path = "src/divoid_mcp/version.py"
```

After this there is exactly **one** version literal in the repo. The historical failure — *bump one
file, forget the other* — is not guarded against; it is **structurally absent**, because there is no
second file to forget.

**Why this direction and not the reverse.** The runtime must be able to report its version with no
I/O and no dependency on installation state; a literal in the module satisfies that unconditionally.
The build, by contrast, runs once, on a source tree, with a backend that can read files. Put the
literal where the constraint is hardest, and derive where it is easiest.

**Why it costs nothing.** Hatchling is already the backend (`pyproject.toml:2-3`),
`[tool.hatch.version]` is a first-party feature, and §2.6 shows it does not import the module — so no
build-time dependency on `mcp` or `httpx` is introduced.

## 5. Alternatives, and why they lose

### 5.1 One-time manual re-sync — rejected

Set `pyproject.toml` to `0.11.0` and move on. This is the strongest rejected alternative because it
is genuinely the smallest diff, and it is what #11369 prescribes.

It loses on measurement, not on taste: **prescribed three times (#1434 §Fix, #1638 §Origin,
#11369 §Fix), drifted three times.** #1434 even recorded the right instinct — *"keep `pyproject` and
`__version__` in lockstep, or make one a derivation of the other"* — and the lockstep half was chosen
by default. It then held for six bumps and broke on the seventh. A convention that survives six
opportunities and fails on the seventh is not a control; it is a coin with good odds.

The task's own criterion settles it: a fix that leaves the mechanism intact has not addressed the task.

### 5.2 `version.py` reads from installed package metadata — rejected, on measurement

`__version__ = importlib.metadata.version("divoid-mcp")`, making `pyproject.toml` canonical. This is
#1638's "Option A", and it is the alternative that sounds most correct until it is run.

Two measured failures:

- **In an editable dev install it reports the stale install-time value.** The repo's own
  `divoid-mcp/.venv` holds `divoid_mcp-0.10.0.dist-info` plus `_editable_impl_divoid_mcp.pth`
  pointing at live source. Metadata is frozen at install time while the code is live, so:
  `metadata version: 0.10.0` / `module __version__: 0.11.0` — **the same 0.10.0-vs-0.11.0 skew this
  task exists to close, merely relocated**, and now un-fixable by editing a file: it needs a reinstall.
- **From a source tree with no install it does not resolve at all.** In a fresh venv:
  `PackageNotFoundError: No package metadata was found for divoid-mcp`. That would have to be caught
  and defaulted, and a default *is* a second literal — the very thing being removed.

It also inverts the drift-pin ergonomics for no gain: `PINNED_API_REF_HASH` lives in `version.py`
(#6108 §3), so this option puts the two version-adjacent facts in two files while the mechanism it
replaces put them in one.

### 5.3 Split `PINNED_API_REF_HASH` into its own module — rejected

Considered because the brief asks whether packaging and the drift canary should keep sharing a file.
They should.

- **Nothing forces the split.** §2.6 proves hatchling never imports or evaluates the file, so the pin
  imposes no constraint on the build and the build imposes none on the pin.
- **The maintenance verb keeps working untouched.** #6108 §3's procedure — re-fetch node #8, replace
  the constant, commit — is unaffected, because `version.py` is not edited by this change at all.
  The three places that name that path (`docs/drift-policy.md:48`, `tests/smoke/README.md:93`,
  `README.md:109`) stay correct with no edit.
- **KISS, delete-check:** what breaks if the split is absent? Nothing. What does it cost? A new file,
  an import change in `drift.py:26`, and edits to three docs and `CLAUDE.md:41` — churn with no
  measured benefit. Per #1136 §4 that is an indirection, not an abstraction.

The two constants are related, not merely co-located: both answer *"what is this build pinned to"*.
One is the package's own identity, the other the API contract it was built against. Sharing a
twenty-line module is the honest shape.

### 5.4 Renumber to 0.12.0 — rejected

The resolved version becomes **`0.11.0`** with no renumber, agreeing with #11369's explicit ruling:
*"the merged code is what `0.11.0` names, and renumbering it after the fact would make the one machine
already carrying it wrong instead."*

And it is unambiguous in the direction that matters. Every wheel buildable from any commit up to
`df3436b` reports **≤ 0.10.0** in metadata — measured in §2.4 (`…-0.10.0-…whl`). So after this change,
`pip show divoid-mcp` reporting `0.11.0` identifies a post-fix install exactly, with no third state
to disambiguate.

## 6. What does not change

`src/divoid_mcp/version.py` — **not edited by this change.** `__version__`, `PINNED_API_REF_HASH`, the
docstring and the maintenance procedure it documents all stay byte-for-byte as they are. No runtime
module changes: `server.py:31,49,67`, `__init__.py:9`, `drift.py:26` and the smoke suite continue to
import exactly what they import today.

## 7. The guard — and the hazard of one that cannot fail

The task asks explicitly what the guard is, and warns that *"a version-consistency check that can
only pass is worth naming as a hazard rather than shipping."* That hazard is live in this repo
already, so it is named first.

### 7.1 Two checks that must NOT be counted as version-consistency guards

- **`smoke_server_bootstrap` (`tests/smoke/run_all.py:3334,3361`) is self-referential.** It does
  `from divoid_mcp.version import __version__ as _version` and then asserts the subprocess logged
  `f"divoid-mcp {_version} starting."` — comparing the module to itself, through a process that reads
  the same module. It cannot observe packaging skew, and it did not: the skew has been live since
  2026-09-03 and this suite is unaffected. It is a genuine and valuable *FastMCP-constructor* guard
  (its docstring says so, citing the `version=__version__` `TypeError`). It is not this guard.
- **Do not write "read `src/divoid_mcp/version.py`, compare to `divoid_mcp.__version__`".** In a
  repo-run those are the same file. It is a tautology that reads like a measurement — the exact shape
  the hazard describes.

The discriminating question for either: *would this still pass against a tree where `pyproject.toml`
carries a conflicting static version?* Both answer yes. Neither is coverage.

### 7.2 Guard A — the build backend, free and already proven

§2.7: re-adding a static `version` next to `dynamic` makes the package **unbuildable** with
`ValueError: Metadata field 'version' cannot be both statically defined and listed in field
'project.dynamic'`. This is PEP 621 enforced by hatchling, not something we write or maintain, and it
covers the accidental-reintroduction case absolutely.

Its limit, stated because it is the whole reason Guard B exists: it does **not** catch a *clean*
reversion — deleting `dynamic` and `[tool.hatch.version]` and restoring a static literal builds fine.

### 7.3 Guard B — a structural unit test, observed red/green/red

New file `tests/unit/test_version_single_source.py`. Parse `pyproject.toml` with `tomllib` (stdlib,
§2.9) resolved as `Path(__file__).parents[2] / "pyproject.toml"`, and assert three things:

1. the `[project]` table carries **no** `version` key;
2. `"version"` is present in `[project].dynamic`;
3. `[tool.hatch.version].path == "src/divoid_mcp/version.py"`.

**I ran this logic against three trees before writing it down.** Observed, not predicted:

| Tree | Result |
|---|---|
| current `divoid-mcp/` at `df3436b` | **FAIL** — `[project] carries a static version = '0.10.0'`; `dynamic` missing `version`; `[tool.hatch.version].path is None` |
| the fixed tree (§2.5) | **PASS** |
| the fixed tree, mutated by reverting to a static version and deleting `[tool.hatch.version]` | **FAIL** — same three messages |

The mutation in row 3 *is* the failure mode: it is precisely what "someone tidies pyproject back to a
normal-looking static version" does. It was applied and observed red.

**Falsifier for this guard:** an implementation that keeps `dynamic = ["version"]` but points
`[tool.hatch.version].path` at some *other* file also defining `__version__`. Assertion 3 catches
that by exact string, at the cost that a deliberate move of `version.py` turns the test red until it
is updated — which is the correct behaviour, since moving the single source is exactly the decision
that deserves a deliberate edit.

**What it structurally cannot reach:** it reads the *declaration*, not a built artifact. It would not
notice a hatchling upgrade that changed how the declaration is interpreted. The end-to-end check for
that is a build (§2.5), which has no automated host (§2.8) and is not worth a bespoke one for this;
the operator's existing post-merge install verification already covers it.

**KISS, delete-check on Guard B.** What breaks if it is absent? Guard A still blocks the *additive*
mistake, but the clean reversion reopens the drift silently. Given three recurrences in three months
(§2.2), ~20 lines with no new dependency is proportionate. It is the only element of this design that
is not a deletion, and it earns its place on that count.

## 8. Implementation guidance for john-backend-dev

One PR. No runtime code changes.

1. **`divoid-mcp/pyproject.toml`** — replace line 7 `version = "0.10.0"` with `dynamic = ["version"]`.
   Add, adjacent to the existing `[tool.hatch.build.targets.*]` tables (TOML table order is
   immaterial; keeping the `tool.hatch` tables together is the readability reason):

   ```toml
   [tool.hatch.version]
   path = "src/divoid_mcp/version.py"
   ```

   No `pattern` key is needed — hatchling's default regex matches the `__version__ = "…"` form
   already in the file (§2.5).

2. **`divoid-mcp/src/divoid_mcp/version.py`** — **no change.** If your diff touches this file,
   something has gone wrong.

3. **`divoid-mcp/tests/unit/test_version_single_source.py`** — new, per §7.3. One test function is
   enough; keep the three assertions separate so a failure names which invariant broke.

4. **`divoid-mcp/CLAUDE.md:41`** — the repo-layout line currently reads
   `version.py           # __version__ + PINNED_API_REF_HASH`. Amend it to say this file is the
   **single source** of the package version and that `pyproject.toml` derives it. This is the only
   place in the subtree that describes the file's role, and there is no documented release/bump
   procedure anywhere (measured: `grep -rn -i "bump\|release\|version" CLAUDE.md` returns that line
   and nothing else), which is part of why the convention had nothing holding it.

5. **Verify, and quote the output in your report** — the claims in §2.5 are the acceptance criteria:

   ```
   python -m pip wheel --no-deps -w dist .        # expect: divoid_mcp-0.11.0-py3-none-any.whl
   python -m pytest tests/unit                    # expect: green, incl. the new test
   ```

   Do not report the version as fixed on the basis of the diff alone. Both prior QA passes on this
   defect missed it for exactly that reason — **#11369 §Provenance**: *"Not caught by either PR's QA,
   because both reviewed the diff and neither built a wheel."*

**Not in this PR:** any edit to `version.py`; any change to `PINNED_API_REF_HASH`; any renumber to
0.12.0 (§5.4); any change to `docs/install.md:27` (§2.1).

## 9. Pre-Design Checklist (#1136 §5)

| Item | Answer |
|---|---|
| New type mirroring an existing one | None. The change is a net deletion of one literal. |
| New abstraction with one implementation | None. |
| Element justified by "we might need X later" | None. Guard B is justified by three past recurrences, not a hypothetical. |
| Deprecation period / shim / transition window | None. The change is atomic in one commit. |
| DRY math | `block_size × site_count` = **1 × 2** — one line of fact, duplicated at two sites. Below the ~15-20 threshold as raw volume, but that threshold governs *whether to extract a helper*, not whether to tolerate a hand-copied fact with a measured three-event failure history. The deduplication here is free (the backend already supports it), which removes the trade-off the threshold exists to arbitrate. |
| Existing surface audited first | Yes — hatchling was already the build backend; nothing new is introduced. |
| New layer justified | No new layer. |
| New config knob | None. |
| Can-it-be-deleted / merged / inlined | Applied to every element. The split of `PINNED_API_REF_HASH` (§5.3) failed the delete-check and was dropped. Guard B passed it on the recurrence count (§7.3). |
| Trade-offs named explicitly | §5.1–5.4, each with the measurement that decided it. |
| Out-of-scope items listed | §3. |
| Cites #114 and #1136 as load-bearing | Yes — #114 governs this repo including its Python (per QA #13153 CF-7); the load-bearing-test discipline (#275) is applied in §7.3, where the guard was observed red before being written down. |
| Superseded predecessor marked | This design supersedes the *remedy* in #1434 / #1638 / #11369, not a design document. Per #11228 Lesson 3 those are retracted **in place**, not deleted — see §11. |

## 10. Risks and limits

| Risk | Assessment |
|---|---|
| Hatchling's version source behaves differently in a real `pip install git+…` than in a local build | Low. Both paths invoke the same backend in an isolated build env; the local build in §2.5 fetched hatchling exactly as a git install would. Not separately measured over the network — stated as a limit, not claimed as verified. |
| `version.py` gains an import and breaks the build | Cannot happen: §2.6 measured a build succeeding with an unresolvable `import httpx` in the file. |
| A future hatchling major changes `[tool.hatch.version]` | Possible; `requires = ["hatchling"]` is unpinned. Out of scope, and the failure would be a loud build error, not silent drift. |
| The operator's currently-installed 0.10.0-metadata package | `pip install --upgrade` from git compares 0.10.0 < 0.11.0 and upgrades cleanly. No manual uninstall needed. |
| Guard B tautology | Explicitly designed against; see §7.1 for the two shapes rejected and §7.3 for the observed red/green/red. |

**A falsifiable statement of the central claim, per the "state what would break it" rule.** The claim
is: *after this change the packaged version and the running version cannot disagree.* What would
falsify it: any second place that states the version independently. I swept for that in §2.1 and
found one candidate (`docs/install.md:27`), which is sample output rather than a source. The claim is
therefore bounded as: **within the `divoid-mcp/` subtree, no second authored version literal exists.**
It says nothing about a version recorded outside this repo — an MCP client config, a DiVoid node, a
README elsewhere — and I did not search outside the subtree.

## 11. Open questions for the operator

1. **#11369 is a duplicate of #13109** (same defect, same version pair, filed independently five days
   apart). Both are open. Recommend closing one against the other rather than letting two tasks track
   one fix.
2. **#1434 and #1638 are open and stale.** #1638 already chose this mechanism in June and was never
   implemented. Both, plus #11369, carry a `[tool.setuptools.dynamic]` recipe that **fails against
   this repo's hatchling backend** (§2.3). Per #11228 Lesson 3 the right treatment is retraction in
   place — mark the recipe `WITHDRAWN` with the reason and the fact that falsified it — not silent
   deletion, since an implementer may already have read it. I have not edited those nodes; they are
   task nodes and yours.
3. **Should #6108 gain a line naming the version's single source?** The concept node documents
   `version.py` as holding "`__version__` + `PINNED_API_REF_HASH`" (§3, §Where this lives), which
   stays true, but it does not say the build derives from it. A one-sentence amendment after this
   PR merges would keep the concept node current. Not done pre-emptively — the node should describe
   shipped code.

## 12. Provenance

Design by sarah-software-architect for DiVoid **#13109**, 2026-09-07. Filed to DiVoid as node
**#13169**. Repo-map root #5860 was used
for scope discovery; every claim is pinned to `file:line` or to quoted command output, resolved
against `main` @ `df3436b`. Scratch trees under `C:\dev\claude\_scratch\mcpver-*` were throwaway
copies and carry nothing unique.
