# divoid-mcp — install and registration: point a host at one executable

> Canonical copy: this file. The DiVoid node carries the same document verbatim, not an abstract of it.
> Citations resolve against the working tree at `C:\dev\claude\divoid` on 2026-09-07. Every install measured below was performed against the repository's remote `HEAD`, measured unauthenticated as `f8991d2b0ab29bc51612c76eb6c44355fec98ea2`.
> Source task: DiVoid **#13221**. Diagnosis it builds on: **#13218**. Constraints it must not break: **#6108 §4**, **#13108**.
>
> **Post-implementation note (2026-09-08, QA #13279 W-2):** the design below is now shipped. `config.py:37` citations throughout mark the design-time location of `_ENV_HINT`'s registration tail; the shipped fix spans `config.py:28-45` with the registration line itself at `:43`. §12's incident is historical — the operational install it describes as removed was restored (see #6108's dated append). §10's console-script coverage row and §13 Q3 are both answered: the smoke case was added. None of this changes any decision in the body; it is a citation re-resolve per this document's own §15 step 8.

## TL;DR

**What.** One blessed registration: `command` is the **absolute path** of the `divoid-mcp` console script inside a dedicated venv at `~/.divoid-mcp/venv`; `args` empty; credentials unchanged in `env`.

**How.** Nothing is built — `pyproject.toml:17-18` already ships `[project.scripts] divoid-mcp`. Three commands (venv, `pip install` from git, self-check), then register the path. `docs/install.md` is rewritten around it, deleting line 31, which today tells operators to *ignore* the console script. One string constant in `config.py` (`_ENV_HINT`, shipped at `:28-45`, registration tail at `:43` — was `:37` at design time); `examples/.mcp.json:4` takes an absolute path, not a bare name.

**Cost.** One venv per machine — measured 21 s from zero. No dependency, nothing published, no code written.

**Rejected.** `uvx` / `pipx` — re-introduces the PATH-resolved launcher being removed; neither is installed here. PyPI — no release process, and not published (measured 404).

**Stated limit.** A **moved or renamed** venv still fails silently: exit 1, empty stdout, **empty stderr** (measured). This design removes the common silent failure, not every one.

---

## 1. Problem statement

Toni, verbatim, 2026-09-07, after a long debug session getting divoid-mcp running under the Hermes MCP host (DiVoid #13221):

> "okay - the mcp issue is resolved. it somehow needed a very special call to actually start it -> `/opt/data/DiVoid/divoid-mcp/.venv/bin/divoid-mcp` - everything else failed.
> but it would be nice if setup somehow was easier, that was a long debug session with very little info on what i have to do. is there a way to just point it at an executable and done? This whole installation process is very awkward."

That is **two** complaints and this document answers both separately:

1. **The registration form that works is not the one documented.** The console script worked; `python -m divoid_mcp` — what `docs/install.md` prescribes — did not.
2. **The failure told him nothing.** A config mistake presented as "Connection Closed" and cost a debug session.

### The honest headline

**The console script is already the answer, and the documentation actively argues against it.** `divoid-mcp/docs/install.md:31`, verbatim:

> **If pip prints "WARNING: divoid-mcp.exe is not on PATH":** ignore it. We do not need the `divoid-mcp` script to be on PATH; we run the server as `python -m divoid_mcp` instead, which always works.

That sentence is false on its second clause and it is the direct cause of the debug session: it tells the operator to discard the one message that names where the executable lives, in favour of a form that resolves through PATH and site-packages. **This is not a distribution project.** It is a documentation defect with a two-line packaging consequence, and inflating it would be the failure mode #1136 §1 exists to prevent.

---

## 2. Scope and non-scope

**In scope**

- The blessed registration form and the install procedure that produces it.
- `divoid-mcp/docs/install.md` — rewritten; it is the artifact that failed.
- `divoid-mcp/README.md`, `divoid-mcp/CLAUDE.md`, `divoid-mcp/examples/.mcp.json` — the other sites that teach the fragile form.
- `divoid-mcp/src/divoid_mcp/config.py` (`_ENV_HINT`, shipped at `:28-45`, registration tail at `:43` — was `:37` at design time) — one string constant, plus a test that pins it.
- Reconciling DiVoid **#829** with the repo file: they are two divergent documents today (§4.4).
- What a **wrong registration** shows the operator (§6).

**Out of scope**

- **Any change to credential resolution.** #13108 is authoritative and unchanged: the `env` block carries `DIVOID_MCP_URL` / `DIVOID_MCP_API_KEY`, the fallback file keeps working. This design changes `command`, never `env`.
- **Argument parsing / a `--check-config` or `--version` mode.** #13108 §6.2 rejected it and nothing here reopens that. Observed in passing and left alone: `divoid-mcp --help` silently ignores the argument and starts the server (measured, §3 run I).
- **Publishing to PyPI or the MCP registry.** §7.3 rejects it now; it is a named future, not a member of this phase.
- **Bundling into a standalone binary** (PyInstaller and kin). §7.4.
- **#13220** — an uncaught exception in `http_client.init()` producing a raw traceback. That is *what the server prints*; this document is about *what the host is pointed at* and what it shows when that is wrong. The two are complementary and neither blocks the other. #13221 draws the same boundary: *"[[#13220]] covers a related but distinct gap … this is about the host-facing experience of a bad registration."*
- **The code-map coverage gap.** `docs/install.md` is unreachable from repo-map root #5860, which is exactly why a reader walking the map never finds it. That decision belongs to **#13166**, which already owns it and prescribes the two outcomes (build the nodes, or record the scope boundary on #5860). §11 carries the recommendation; this design does not touch the map.
- **Retries, caching, the drift canary, `paths.py`** — untouched.

### 2.1 Does this engage the deliberate-surface-evolution clause?

`divoid-mcp/CLAUDE.md:15`: *"New tools require human sign-off from the repo owner before implementation — this is a generic-purpose tool used outside this deployment, so the surface evolves deliberately."*

**Strictly, no.** That clause governs the **tool surface** — the set of registered MCP tools and their schemas. This design registers no tool, removes none, renames none, and changes no tool's parameters. The 22-tool surface is byte-identical before and after; what changes is how the same server is *launched*, plus one line of a diagnostic string.

**But it engages the clause's reason, and that is worth saying rather than hiding behind the letter.** The reason the surface evolves deliberately is that people outside this deployment depend on it. `docs/install.md` is *the* artifact those people follow, and this design changes the install contract it publishes: a venv appears where none was required, and the blessed `command` changes shape. An outside user who re-reads it gets different instructions. That is a published-contract change even though it is not a tool-surface change.

**So: not blocked by the clause, and worth Toni's explicit yes on the same grounds.** The specific thing to say yes or no to is §5 — one blessed per-user venv path — since everything else follows from it.

---

## 3. What was measured

Every command in this document was run. Machine: Windows 11, `C:\Python314\python.exe` (Python 3.14.2), pip 26.2.1. Throwaway venvs under `C:\dev\claude\_scratch\mcpinstall-k4z\`, since removed. **The operational MCP registration was not modified.** (One measurement did damage the operational *install*; it is reported in §12.)

| # | What was run | Observed |
|---|---|---|
| **M1** | `cat divoid-mcp/pyproject.toml` | `[project.scripts]` at `:17`, `divoid-mcp = "divoid_mcp.__main__:main"` at `:18`. **The console script already exists; nothing has to be built.** |
| **M2** | `curl -s -o /dev/null -w '%{http_code}' "https://github.com/telmengedar/DiVoid.git/info/refs?service=git-upload-pack"` with **no** `Authorization` header | `200`, advertising `f8991d2b0ab29bc51612c76eb6c44355fec98ea2 HEAD`. **The repository is public.** `divoid-mcp/CLAUDE.md:69`'s *"private repo → prefix the host with a token"* is stale. |
| **M3** | `python -m venv <d>` then `<d>/Scripts/python.exe -m pip install "git+https://github.com/telmengedar/DiVoid.git#subdirectory=divoid-mcp"`, timed, no credentials supplied | exit 0 in **21.4 s** total. Produced `<d>/Scripts/divoid-mcp.exe`. `pip show` → `Version: 0.11.0`, no `Editable project location:` line. |
| **M4** | Byte-scan of `divoid-mcp.exe` for its embedded shebang | `#!C:\dev\claude\_scratch\mcpinstall-k4z\tv\Scripts\python.exe` — **the launcher carries the absolute interpreter path**. `entry_points.txt` reads `divoid-mcp = divoid_mcp.__main__:main`. |
| **M5** | Spawn the console script **by absolute path**, `PATH=C:\Windows\System32`, no `DIVOID_*` vars, `HOME`/`USERPROFILE` redirected to an empty directory | exit **1**, **stdout 0 bytes**, stderr **1060 bytes** carrying the full #13108 fail-closed diagnostic, naming both variables, the `claude mcp add` line and the resolved fallback path. |
| **M6** | Same, with `DIVOID_MCP_URL=http://127.0.0.1:9/api` + `DIVOID_MCP_API_KEY=<fake>`, one `initialize` JSON-RPC line on stdin | exit **0**, `Config loaded: source=env`, **545 bytes of clean JSON-RPC on stdout**, one line, no non-JSON. Canary logged *"Drift check skipped: DiVoid unreachable"* and did not block. |
| **M7** | Same, with **`PATH=""`** (completely empty) | Reaches the credential branch identically. **PATH plays no part.** |
| **M8** | Same, with `PYTHONNOUSERSITE=1` and `PYTHONPATH=""` | Identical. **User-site visibility plays no part.** |
| **M9** | `<bare venv>/Scripts/python.exe -m divoid_mcp` under an interpreter that lacks the package | exit **1**, **stdout 0 bytes**, stderr **91 bytes**: `No module named divoid_mcp`. The child spawned successfully and then died — from the host's side, indistinguishable from any other silent exit. |
| **M10** | Spawn a **wrong** absolute path (`divoid-mcpp.exe`) | **The spawn itself fails** — `OSError` `errno=2 winerror=2`, *"Das System kann die angegebene Datei nicht finden"*. No child ever exists. |
| **M11** | Rename the venv directory so the launcher's embedded interpreter path no longer resolves, then run the script from its new path | exit **1**, stdout **0 bytes**, **stderr 0 bytes**. **Completely silent.** |
| **M12** | `uv --version`, `uvx --version`, `pipx --version` | all three: `command not found`. |
| **M13** | `curl -s -o /dev/null -w '%{http_code}' https://pypi.org/pypi/divoid-mcp/json` | `404` — not published. |
| **M14** | `<venv>/Scripts/python.exe -m pip install --force-reinstall "git+…"`, timed | exit 0 in **17.9 s**; still non-editable; console script still present. |
| **M15** | Fetch node **#829**, diff against `divoid-mcp/docs/install.md` | node: **19,764 bytes**, sha256 `0e435bbff3b81d97b75a6319ffb7808ca01079eb17d1c22befa44eb24d4387e4`. File: **14,535 bytes**, sha256 `c45675a90735d0fbb1a5296a90d6a8803be473e94a15ff268ef43c942b3dde56`. `diff -u` produced **one whole-file hunk** (`@@ -1,311 +1,218 @@`). **They are two different documents.** |
| **M16** | `grep` for a console-script registration in each copy | Node #829 has one; the repo file does not. Node #829, §*Bekannte Probleme / Multi-Account-Macs*: `"command": "/Users/<you>/.local/bin/divoid-mcp"` with the note *"Use the absolute path — `~` may not expand inside the JSON."* |
| **M17** | Locate the operational install's console script | `C:\Users\max_g\AppData\Roaming\Python\Python314\Scripts\divoid-mcp.exe` — present, in a directory **not on PATH**. Its `dist-info` read `0.10.0` while the module reported `0.11.0` — the same skew #13218 §4 recorded, i.e. an artifact of a stale install rather than of the packaging (#1638, *"unify version source"*, and #13109 are both **closed**; PR #187 moved the version to a single source). |
| **M18** | `sysconfig.get_path('scripts')` and `site.getuserbase()` under that same interpreter | `C:\Python314\Scripts\divoid-mcp.exe` → **does not exist**; `C:\Users\max_g\AppData\Roaming\Python\Scripts\divoid-mcp.exe` → **does not exist**. Both name the wrong directory for a per-user install. |
| **M19** | `from divoid_mcp.config import _ENV_HINT; 'python -m divoid_mcp' in _ENV_HINT` against the installed 0.11.0 module | `True`. The hint the fail-closed message shows a foreign environment currently teaches the fragile form. |
| **M20** | `claude mcp add --help` | `Usage: claude mcp add [options] <name> <commandOrUrl> [args...]`, with `-e, --env <env...>`. An absolute path is accepted as `<commandOrUrl>`. |

**Re-derivation rule for the site list in §8** — do not trust it to be complete: run
`grep -rn "python -m divoid_mcp\|\"-m\", \"divoid_mcp\"\|-m divoid_mcp" divoid-mcp/ --include='*.md' --include='*.json' --include='*.py' --include='*.toml' | grep -v '\.venv/'`
and treat every hit as a candidate.

---

## 4. Decision 1 — the registration form

### 4.1 The form

```json
{
  "mcpServers": {
    "divoid": {
      "command": "/home/<you>/.divoid-mcp/venv/bin/divoid-mcp",
      "args": [],
      "env": {
        "DIVOID_MCP_URL": "https://divoid.mamgo.io/api",
        "DIVOID_MCP_API_KEY": "<your-divoid-api-key>"
      }
    }
  }
}
```

Windows: `"command": "C:\\Users\\<you>\\.divoid-mcp\\venv\\Scripts\\divoid-mcp.exe"`.

**One absolute path. No interpreter. No module name. No PATH. No working directory.** That is exactly the shape #13221 asks for, and `env` is untouched, so #13108 is satisfied by construction.

**`~` must be expanded.** JSON configuration files do not expand it. Node #829 already carries this warning; it must survive into the canonical copy.

**Why the bare name `divoid-mcp` is not enough.** `examples/.mcp.json:4` currently uses `"command": "divoid-mcp"`. That resolves through PATH — the same class of failure this design removes, and the class pip's ignored *"is not on PATH"* warning is about (M17: the operational install's script sits in a directory that is not on PATH). The example must carry an absolute path with a placeholder home.

### 4.2 Why the console script is PATH-proof, and what falsifies that

A console script is not a shell wrapper. pip generates it at **install** time from `entry_points.txt` and binds it to the interpreter it was installed into: on POSIX as a `#!` shebang, on Windows as a launcher `.exe` with the interpreter path embedded (M4, measured: `#!C:\…\tv\Scripts\python.exe`). Every failure mode #13218 enumerated under cause 2 is a property of *resolving* an interpreter and a module, and a console script resolves neither.

Measured, not asserted: with `PATH=""` (M7) and with `PYTHONNOUSERSITE=1` (M8), the script starts and reaches the credential branch. Toni's own result is the POSIX half of the same measurement.

> **What would falsify "an absolute path always works":**
>
> - **A moved or renamed venv.** The embedded interpreter path is absolute, so relocating the venv breaks it — and it breaks *silently*: exit 1, empty stdout, **empty stderr** (M11). This is the residual silent-failure case and §5 is the mitigation.
> - **A POSIX shebang longer than the kernel's limit** (`BINPRM_BUF_SIZE`, 128 bytes on Linux). `/opt/data/DiVoid/divoid-mcp/.venv/bin/python` is 43 characters, so Toni's install is far inside it; a venv nested under a very deep path is not. Not measured here — stated as a bound, not a result.
> - **A host that will not accept an absolute path as `command`.** Claude Code accepts one (M20). Hermes accepted one — that is Toni's measurement. No host is known to refuse; that is a bounded claim about the hosts observed, not a universal.

### 4.3 The install, which is three commands

```bash
python -m venv ~/.divoid-mcp/venv
~/.divoid-mcp/venv/bin/python -m pip install "git+https://github.com/telmengedar/DiVoid.git#subdirectory=divoid-mcp"
~/.divoid-mcp/venv/bin/divoid-mcp          # self-check — see §6.2
```

Windows: `%USERPROFILE%\.divoid-mcp\venv`, `\Scripts\python.exe`, `\Scripts\divoid-mcp.exe`.

`python -m pip` rather than `<venv>/bin/pip` is what was measured (M3) and it is the more robust of the two. Measured cost from zero: **21.4 s**. No credentials are needed — the repository is public (M2), so `divoid-mcp/CLAUDE.md:69`'s token prefix is stale and its removal is part of §8.

Update, in place, one command (M14, 17.9 s, stays non-editable):

```bash
~/.divoid-mcp/venv/bin/python -m pip install --force-reinstall "git+https://github.com/telmengedar/DiVoid.git#subdirectory=divoid-mcp"
```

### 4.4 What is *not* prescribed, on measured grounds

**A "find your existing install" one-liner.** The obvious candidate —
`python -c "import sysconfig,os;print(os.path.join(sysconfig.get_path('scripts'),'divoid-mcp'))"` —
names a path that **does not exist** on this machine's own operational install, and so does the `site.getuserbase()` variant (M18). A discovery command that is wrong on the configuration we are steering people away from is worse than no command, because its output looks authoritative. **The doc states the blessed path instead, and tells an operator with an install elsewhere to create the blessed venv rather than hunt for the old one.** The old install can be removed afterwards; it costs a few tens of megabytes and nothing else.

---

## 5. Decision 2 — where the venv lives

**`~/.divoid-mcp/venv`**, on every OS.

| Property | Why it decides |
|---|---|
| **Outside every git checkout** | Toni's working path, `/opt/data/DiVoid/divoid-mcp/.venv`, is *inside* the repository. It works today and it is a trap: a `git clean -xdf`, a re-clone, or a moved checkout destroys or relocates it — and M11 says relocation fails **silently**. It also puts `pip install -e .` one command away in the same directory, which #6108 §4 forbids for an operational install. |
| **Per-user, no elevation** | `/opt` needs root; `%ProgramFiles%` needs admin. A per-user path installs the same way on a laptop and a server. |
| **Fixed and stated** | The whole design rests on the operator knowing the absolute path without discovering it (§4.4). A blessed constant is the cheapest way to know it. |
| **Not `~/.claude/…`** | divoid-mcp is a generic tool used outside this deployment (`divoid-mcp/CLAUDE.md:15`). Its install must not live inside another product's directory. |

**Existing installs are not broken by this.** Anyone already running `python -m divoid_mcp` against a working per-user install keeps working — nothing in the server changes. The blessed path is what the documentation teaches from now on, and what a broken install is migrated to.

---

## 6. Decision 3 — what a wrong registration shows

This is complaint #2, and it needs one honest sentence first:

> **We cannot make an MCP host display the child's stderr.** #13218 measured that the server already writes a complete diagnostic there and exits 1. Hermes showed "Connection Closed" anyway. No change on our side alters that.

So the answer is not a better message. It is to move failures **out of the silent class**, and to make the remaining silent case reproducible in one command.

### 6.1 Where each failure lands under the new form

| The operator got wrong | Under `python -m divoid_mcp` | Under the absolute path | Class |
|---|---|---|---|
| The command itself (typo, wrong path, package not installed for that interpreter, host runs as another user, `PYTHONNOUSERSITE`, `-s`/`-I`) | Child **spawns**, then exits 1 with 91 bytes on stderr the host may never show (M9) — presents as "Connection Closed" | **The spawn fails** before a child exists: `errno 2` / `WinError 2`, with the offending path in the message (M10) | **Silent → loud.** A spawn failure is an error on the *host's own* call, so every host reports it, and it names the path. |
| Credentials missing or half-set | exit 1, full #13108 diagnostic on stderr | **identical** (M5) — unchanged, by design | Unchanged |
| Venv moved or renamed | n/a | exit 1, **stdout and stderr both empty** (M11) | **Still silent.** Mitigated by §5, not eliminated. Named, not hidden. |

The first row is the whole gain, and it is free: it comes from the host's own error reporting rather than from anything we write.

### 6.2 The self-check, and why the old one did not work

`docs/install.md:148` tells a stuck operator to run `python -m divoid_mcp` in a terminal. **That is the reason the debug session was long.** It runs a *different* command in a *different* environment from the one the host spawns — the operator's shell has their PATH, their user, their site-packages. It can succeed while the host's spawn fails, which teaches the operator that the server is fine and the host is broken.

The replacement is reproducible by construction, because the registered command is now a single absolute path with nothing left to resolve:

> **Copy the `command` value out of your host's configuration and run it verbatim.** Expected output (measured, M5): two `INFO`/`ERROR` lines naming the missing credentials, then exit. If instead you see nothing at all, the venv has been moved — recreate it per §4.3.

That instruction is worth more than any message we could add, and it costs one paragraph.

### 6.3 The one string that must change in code

`config.py:37` rendered (design-time citation; the shipped fix moved this to `:28-45`, registration tail at `:43`), inside the `_ENV_HINT` block that every fail-closed branch appends:

```
      -e DIVOID_MCP_URL=<url> -e DIVOID_MCP_API_KEY=<key> -- python -m divoid_mcp
```

Measured live (M19): the shipped 0.11.0 hint contains `python -m divoid_mcp`. This is the **primary discovery instrument** for a foreign environment — #13108 §6 route 1 — so leaving it teaching the fragile form defeats the design at the exact point it matters most. It becomes the absolute-path form, with the blessed path as the placeholder.

**Nothing else in the code changes.** No new branch, no new module, no argument parsing. One string constant and a test.

---

## 7. Rejected alternatives

Each is rejected on a measurement or a named cost, per #1136 §1.

### 7.1 Keep `python -m divoid_mcp` and only fix the docs
Rejected. The form is fragile for reasons no documentation removes (#13218 cause 2; M9). Documenting it better would still leave the failure in the silent class (§6.1 row 1).

### 7.2 `uvx` / `pipx run` as the `command`
Rejected on two independent grounds.
- **It re-introduces the defect.** `"command": "uvx"` is a bare name resolved through the host's PATH — the same resolution step that just cost a debug session. Registering an absolute path to `uvx` merely moves the problem one level down, and `uvx` then resolves the *package* at launch.
- **It adds a prerequisite that is absent.** `uv`, `uvx` and `pipx` are all `command not found` on this machine (M12), which is the reference deployment. Node #829's multi-account-Mac section documents a `uv tool install` route that legitimately solves a *macOS multi-account* problem; that is a special case to preserve (§8), not the default to bless.
- Secondary: `uvx --from git+…` re-resolves per launch, so the pin (#6108 §4) becomes a property of the registration string rather than of an install.

### 7.3 Publish to PyPI (then `pipx install divoid-mcp` / `uvx divoid-mcp`)
Rejected **now**, named as a future. It is the shape that would genuinely make install one command with no venv step. But: the package is not on PyPI (M13), no release process, versioning policy, or publish credentials exist, and the graph holds **no prior decision or task on publishing at all** — this would be a new commitment, not an implementation detail. It also does not remove the absolute-path question: `pipx` still writes a launcher somewhere and the registration still wants its path. **Named future, prose only — no member of this phase anticipates it.**

### 7.4 Bundle into a standalone executable (PyInstaller, shiv, zipapp)
Rejected.
- **PyInstaller:** per-platform CI builds, ~30–50 MB artifacts, a hosting decision, and no update path better than "download the new one". A distribution mechanism nobody maintains is the precise failure #1136 §1 warns about, against a problem whose actual fix is deleting one sentence from a document.
- **`zipapp` / `shiv`:** the `.pyz` still needs an interpreter to launch it, so `command` becomes `python foo.pyz` — the PATH dependency returns, unchanged.

### 7.5 A decision table of supported registrations instead of one blessed path
Rejected. #13221 asks for *"point it at an executable and done"*. A table is the artifact people skim past, and every additional row is a row someone will pick wrongly. **One blessed path**, with §4.2's falsifiers and §5's rationale stated so a reader who must deviate knows what they are trading. The one genuine special case — macOS multi-account, node #829 — stays as a clearly-labelled exception, not as a peer option.

---

## 8. Change inventory

Ordered; one PR. **Not a complete list by assertion** — re-derive with the grep in §3.

| # | Site | Change |
|---|---|---|
| 1 | `divoid-mcp/src/divoid_mcp/config.py:28-45` (registration tail `:43`; was `:37` at design time) | `_ENV_HINT`'s registration line names the absolute-path console-script form. |
| 2 | `divoid-mcp/tests/unit/test_config.py` | New assertion that the rendered hint names the console-script form and does **not** contain `python -m divoid_mcp`. Existing `:140` / `:298` assert only `"claude mcp add --transport stdio"` and are unaffected. |
| 3 | `divoid-mcp/examples/.mcp.json:4` | `"command"` becomes an absolute path with a placeholder home; `"args": []` stays. |
| 4 | `divoid-mcp/docs/install.md` | Rewritten (§9). **Delete line 31.** Replace `:65`, `:78`, `:95`, `:119-120`, `:148`, `:163`. |
| 5 | `divoid-mcp/README.md:13`, `:63` | Quick path and configuration example take the blessed form. |
| 6 | `divoid-mcp/CLAUDE.md:50`, `:57` | `:50` says the operational MCP is *"registered as `python -m divoid_mcp`"* — becomes the console-script form. `:57` is the **dev** loop inside an isolated venv and is correct as it stands; leave it, and say so in the PR so a later reader does not "fix" it. |
| 7 | `divoid-mcp/CLAUDE.md:69` | Delete the *"private repo → prefix the host with a token"* comment. The repository is public (M2) and the token instruction sends a reader looking for a credential they do not need. |
| 8 | DiVoid **#829** | Reconcile — §8.1. **Read before you overwrite.** |
| 9 | DiVoid **#6108** | Append a note under §4: the pinned-install contract is now satisfied by a **dedicated venv outside every checkout**, and name the reason (M11 — a relocated venv fails silently). |
| 10 | DiVoid **#13108** | Append one line to §6's discovery table and to §7.1's hint block, recording that the `claude mcp add` line now names the console script. #13108 remains authoritative for credentials; this touches only the registration tail of its hint. |

### 8.1 Node #829 — reconcile, do not overwrite

**#829 and `docs/install.md` are two different documents** (M15: different byte counts, different hashes, a single whole-file diff hunk), and neither declares which is canonical. That is the structural cause of complaint #2: **the answer Toni needed was written down** — node #829 registers `"command": "/Users/<you>/.local/bin/divoid-mcp"` and warns that `~` does not expand — **in the copy he did not read**, under a heading about multi-account Macs.

> **Before writing anything to #829, read it in full and diff it against the repo file.** Content that exists only on the node today: *Recent releases*, *Upgrading* (including the `WinError 5` / `--no-deps` guidance and the #1638 metadata-drift note), and *Bekannte Probleme / Multi-Account-Macs*. **Merge those into the repo file first.** Publishing the current repo file over #829 would destroy them — that is the #11228 step-5 hazard, and this document is the last place it can be caught.

Afterwards: the **repo file is canonical**, #829 carries the same bytes, and #829's first line says so. One document, one maintainer.

---

## 9. `docs/install.md` — the shape it takes

Same audience, same tone, restructured so the blessed path is the only path a reader can follow by accident.

1. **What you need** — Python 3.11+, an MCP host, an API key. Unchanged.
2. **Step 1 — Install into a dedicated venv.** The three commands from §4.3, with the Windows column beside the POSIX one. The pip *"not on PATH"* warning is now **expected and irrelevant** (we never use PATH) — say that, and delete the sentence that tells the reader the script is unnecessary.
3. **Step 2 — Credentials.** Unchanged from PR #186; #13108 governs.
4. **Step 3 — Register the absolute path.** One `claude mcp add … -- <absolute path>` line (shape confirmed at M20), one JSON block, one generic-host bullet list. Every one of the three carries the same absolute path.
5. **Step 4 — Verify.** Unchanged.
6. **Troubleshooting**, reorganised around what the *host* shows, because that is all an operator has:
   - *The host says the command was not found / no such file* → the path is wrong or the venv is gone. §4.3.
   - *The host says "Connection Closed" / "Failed to connect"* → run the `command` value verbatim (§6.2). Expected output quoted from M5.
   - *You run it and see nothing at all* → the venv was moved. Recreate it (M11).
   - Existing entries for `401`, `data_entitynotfound`, drift `MISMATCH`, and the credential refusals: kept.
   - Merged in from #829: *Upgrading*, `WinError 5`, the pip-metadata-vs-code skew entry (node #829 titles it after DiVoid #1638, which is closed — keep the *symptom* guidance, drop the implication that the task is open; the same class was observed at M17 on the pre-incident install), and the multi-account-macOS section, relabelled as an explicit exception to the blessed path.
7. **Uninstall / Updating the doc / Architecture** — kept; uninstall becomes "delete `~/.divoid-mcp` and remove the server entry".

---

## 10. Coverage — named guards

Falsifier for this table: **any row whose named guard would still pass against an implementation lacking the claimed property.**

| Property | Guard | Discriminates because | Observed? |
|---|---|---|---|
| The fail-closed hint no longer teaches `python -m divoid_mcp`, and the copy-pasteable recipe itself (not just its explainer line) names the console script | new `tests/unit/test_config.py::test_env_hint_names_the_console_script_form` | The assertion is **RED against shipped 0.11.0** — measured at M19, `'python -m divoid_mcp' in _ENV_HINT` is `True`. Reverting the `_ENV_HINT` block (`config.py:28-45`) reddens it; QA #13279 found the initial version of this assertion checked only the explainer line at `:38` and was satisfied even with the recipe's own command deleted or corrupted (`config.py:43`) — the assertion now pins `"-- ~/.divoid-mcp/venv/bin/divoid-mcp"` (with its `--` separator) specifically, verified red against both mutations before green. | **Yes — M19, plus the recipe-pin fix.** |
| The hint still carries the `claude mcp add` recipe | existing `test_config.py:140`, `:304` | Both assert `"claude mcp add --transport stdio"` is present; deleting the recipe reddens both. | Pre-existing, unchanged by this design. |
| Credential resolution is unaltered | the existing `test_config.py` suite in full | This design touches one line of a *message*; any change to selection or parsing reddens the E1/E2/F1–F5 cases #13108 §7 specifies. | Pre-existing. |
| The console script exists in a built install | `tests/smoke/run_all.py::smoke_console_script_bootstrap` — builds a throwaway venv, installs non-editably, spawns `divoid-mcp`/`divoid-mcp.exe` by absolute path | **Implemented, closing this row.** §13 Q3 answered yes; see the "Post-implementation" note below §13. | **Yes — run standalone, 6/6 assertions pass without live DiVoid.** |
| A registration with a wrong path fails at spawn | **none — this is a host behaviour, not ours** | Measured once (M10) on Windows/CPython. It is a property of `CreateProcess`/`execve`, not of our code, so there is nothing in this repo to guard. | Measured, not guarded. |
| The install procedure works from zero | **none automated** | M3 is a one-off manual run. Automating it means CI with network access to GitHub; not proposed here. | Measured manually (M3, M14; the `git+https` form itself was measured too — QA #13279). |

Two of six rows name no guard, down from three at design time — §13 Q3's smoke case closed the third.

---

## 11. Discoverability — the recommendation, not the change

`docs/install.md` is unreachable from repo-map root **#5860** (#13166). Every route in #13108 §6 that a stranger takes — the refusal message, the README, the example — points at that file, and an agent orienting via the map lands in `config.py` and never reaches it. This design makes the file *more* load-bearing, so the gap gets worse, not better.

**Recommendation, for #13166 to decide and record:** add a `divoid-mcp/docs/` node under #5860 covering `install.md` and `drift-policy.md`, and an `examples/` node covering `.mcp.json`. If the answer is no, record the scope boundary on #5860 as #13166 asks, so the next reconcile does not re-raise it. **This design does not touch the map** — that would be a second feature in one PR.

---

## 12. Incident during measurement — the operational install was uninstalled

Recorded here because it is a fact about the machine the implementer will work on, not a footnote.

While measuring whether pip's *"not on PATH"* warning names its directory, I ran:

```
python -m pip install --no-deps --prefix <scratch> "git+https://github.com/telmengedar/DiVoid.git#subdirectory=divoid-mcp"
```

`--prefix` does **not** isolate the operation: pip uninstalled the existing user-site distribution first. Output: `Successfully uninstalled divoid-mcp-0.10.0`. Verified afterwards — `import divoid_mcp` → `ModuleNotFoundError`; `%APPDATA%\Python\Python314\site-packages\divoid_mcp` and `…\Scripts\divoid-mcp.exe` are gone.

**The restore command was denied by the permission classifier, twice**, at design time. The restore was later applied (see #6108's "Incident 2026-09-07" append, and the implementation pass on #13221): the operational install is **restored** — module and pip metadata both `0.11.0`, console script present, non-editable. `C:\dev\claude\_scratch\mcpinstall-k4z\prefixtest\` no longer exists. This section is a historical record of the incident and the recovery command that fixed it, not a statement of current machine state — do not re-run the reinstall below against a working install on its account.

```
python -m pip install --force-reinstall "git+https://github.com/telmengedar/DiVoid.git#subdirectory=divoid-mcp"
```

**The lesson, which belongs in the doc being written:** `pip install --prefix` and `--target` are not sandboxes — they still uninstall a matching distribution from the environment's normal location. **A venv is the only isolation that holds.** That is an independent argument for §5.

---

## 13. Open questions

1. **The venv path.** `~/.divoid-mcp/venv` is my choice, on the grounds in §5. If there is a house convention for per-user tool installs on the Linux boxes (`/opt/…`, `~/.local/share/…`), it wins — the design depends only on *stable, outside every checkout, stated in the doc*, not on this particular string.
2. **Node #829 vs the repo file.** I have made the repo file canonical (§8.1) because #1220 requires one canonical copy and the repo file is the newer one. #829 is the id everything external cites. If you would rather the node stay canonical and the repo file carry the pointer, that inverts §8.1 and nothing else.
3. **A smoke check for the console script.** `tests/smoke/run_all.py:3343` (content unchanged by this PR; was `:3339` at HEAD before this PR's new imports shifted it +4 -- the design-time citation of `:3337` was never correct, even against HEAD) spawns `python -m divoid_mcp`; nothing exercised `[project.scripts]` at design time. **Answered: yes, worth it — added.** `smoke_console_script_bootstrap` builds a throwaway venv (`--system-site-packages`, `--no-deps` for speed) and spawns the console script by absolute path; run standalone (not part of the live-DiVoid suite, per the implementation brief), 6/6 assertions pass. See §10's updated coverage table.
4. **PyPI (§7.3).** Rejected for this phase on process grounds, not technical ones. If publishing is on the table at all, say so and it becomes the better long-term answer — at which point the MCP registry `server.json` that #13108 §6.1 rejected for having no consumer acquires one.
5. **`divoid-mcp --help` starts the server** (M-run I) rather than printing anything. Harmless, mildly surprising, and out of scope under #13108 §6.2. Leave it, or file a one-line task?

---

## 14. Pre-Design Checklist (#1136 §5)

**KISS / DRY / YAGNI**
- *No new type mirroring an existing one* — ✓ no new types. The deliverable is one string, one JSON value, and documentation.
- *No new abstraction with one implementation* — ✓ none proposed.
- *No element justified by "we might need X later"* — ✓ PyPI and the MCP registry are prose in §7.3 with no member built for them, per #1220's *"a future member is a LIST, never an enum member"*.
- *No deprecation period / feature flag / shim* — ✓ existing installs keep working because nothing in the server's behaviour changes; there is no transition mechanism to build.
- *`block_size × site_count` for every inline decision* — The registration block (~6 rendered lines) recurs at **8** documentation sites (§8 rows 3–7 plus `config.py`'s `_ENV_HINT`, `:28-45`), math **48**, over the ~15–20 threshold. **No helper is specified, and the reason is not the number:** the sites span Markdown prose, a JSON file and a Python string literal, so no extraction target exists across them. This is the identical call #13108 §6.3 made for the same set of sites; the mitigation is the same — one PR touches all of them, listed in §8 with a re-derivation rule rather than a completeness claim.

**Existing systems first**
- *Audited whether an existing surface covers this* — ✓ **it does, and that is the finding.** `pyproject.toml:17-18` already ships the console script (M1). The design adopts it instead of adding anything.
- *If a new layer is proposed, the concrete reason it can't live on the existing surface* — n/a, no new layer.
- *New persisted data point* — n/a.

**Configurability**
- *Every new knob has a named operator* — ✓ **no new knob.** The venv path is a documented constant, not a configuration variable: the operator picks their own path by creating the venv there, and the registration carries it. Nothing reads a variable to find it.

**Less is better**
- *delete / merge / inline check* — Each element was tested: the discovery one-liner was **deleted** (§4.4, M18 shows it is wrong here); a `--check-config` mode was **not added** (#13108 §6.2); the decision table was **collapsed to one path** (§7.5); the code change is **one string**, and could not be smaller without leaving the primary discovery instrument teaching the wrong form (M19).
- *Trade-offs named where a complex design wins over a simpler one* — the simpler design (docs-only, §7.1) is named and rejected on M9.
- *Radical-clean shape where the existing surface has no consumer* — ✓ `docs/install.md:31` is **deleted**, not softened.

**Data deliverables** — n/a, no SQL, no migration, no backfill.

**Document discipline**
- ✓ Cites #114 and #1136 as load-bearing; #114 governs the Python change in §8 row 1–2.
- ✓ Scope and non-scope explicit (§2).
- ✓ Site inventory explicit, with a re-derivation rule and **no completeness claim** (§3, §8).
- ✓ No multi-paragraph rationale for things that obviously stay.
- ✓ No predecessor design is superseded end-to-end. #13108 is **narrowed at one line of one string**, not replaced, and §8 row 10 records that on the node itself.

---

## 15. Implementation order

1. **Restore the operational install** (§12). Nothing else can be verified on this machine until it is back.
2. **Read node #829 in full and diff it against `divoid-mcp/docs/install.md`** (§8.1). Merge the node-only sections into the repo file. Do this **first** — every later step assumes one document.
3. `config.py:37` (design-time; shipped at `:28-45`, registration tail `:43`) — the hint's registration line. Add the `test_config.py` assertion (§10 row 1); confirm it is red before the change and green after — and pin the recipe's own command line specifically, not just its explainer (QA #13279 W-1).
4. `examples/.mcp.json:4` — absolute path with a placeholder home.
5. `docs/install.md` — the rewrite in §9, including deleting `:31`.
6. `README.md:13`, `:63`; `CLAUDE.md:50`, `:69` (leave `:57`).
7. Publish the reconciled `install.md` to **#829**; append the notes to **#6108** and **#13108** (§8 rows 9–10).
8. **Before submitting:** re-resolve every `file:line` and every node id in this document and in every file the branch touches, against the branch commit and against the graph. Four of the citations here name lines in files this PR edits, so they move.

---

## 16. References

- **#13221** — the task and Toni's verbatim ask.
- **#13218** — the Hermes surface enumeration; cause 2 is what Toni hit.
- **#13108** — the credential contract. Authoritative for `env`; unchanged here.
- **#6108 §4** — the pinned-install contract: operational installs are fixed and non-editable.
- **#13166** — the code-map gap that makes `install.md` unreachable.
- **#13220** — startup exception reporting; adjacent, not this.
- **#829** — the operator-facing install guide, divergent from the repo file today.
- **#1136** — Design Contracts. **#114** — Code Contracts, governing the Python change.
