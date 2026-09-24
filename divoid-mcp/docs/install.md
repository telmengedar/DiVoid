# divoid-mcp: install in 5 minutes

> Canonical copy: this file. DiVoid node **#829** mirrors it — if the two ever disagree, this file wins.

The MCP server that gives your agent the `divoid_*` tools — `divoid_search`, `divoid_get_node`, `divoid_get_content`, `divoid_create_task`, `divoid_send_message`, and more (see [`README.md`'s Tools section](../README.md) for the current, authoritative list — it is not reproduced here, see Architecture below). After install, your agent stops calling DiVoid via raw `curl` and starts using these tools, which means UTF-8 works, invariants are checked client-side, and responses come back as structured JSON instead of curl output.

This doc walks **non-technical team members** through the install end-to-end. If you have used the DiVoid REST API once, you already have everything you need. If you have not — you only need an API key (your operator gives you that) and the right MCP host installed (Claude Code, Claude Desktop, or another MCP-compatible tool). The full install takes about five minutes per host once Python is present.

If your agent is the one reading this — you can install it for the human in front of you. Walk them through each step, run the verification command in the last section, and you are done. The team-member-facing language is intentional; the same steps work for you as a subroutine.

---

## Recent releases

| Version | Date | What changed |
|---|---|---|
| **0.9.0** | 2026-09-01 | **Security — filesystem-path containment.** `divoid_download_content(path)` wrote node bytes anywhere the user token allowed and `divoid_set_content(path=…)` read any readable file into the shared graph; neither validated the path (DiVoid #10472, severity 4). Both now pass a gate that resolves the path and requires it to sit under a frozen root — the process working directory by default, overridable with `DIVOID_MCP_FILE_ROOT` (`os.pathsep`-separated). Rejections return `path_outside_root` / `file_root_unusable`. **Behaviour change:** paths outside the session's checkout are now refused, so a workflow writing to a sibling checkout or a shared scratch directory needs an explicit root entry. PR #175 / DiVoid #10473. |
| 0.5.0 | 2026-06-26 | `divoid_create_node` — generic atomic create for ANY node type (type is a parameter, mirrors `POST /api/nodes`); covers meeting / plan / project / group (`type=None`) / custom types the specialized creators don't. Optional UTF-8-safe content + atomic `extra_links`; type-specific creators retained. **Tool count 17→18.** PR #153 / DiVoid #2401. |
| 0.4.0 | 2026-06-04 | `Node.Severity` surface added: response rows include `severity: int \| None`; `divoid_list` accepts `severity` / `severity_min` / `severity_max` / `no_severity` + `sort="severity"`; `divoid_patch_node` whitelists `/severity` + new `clear_severity=True` boolean; `divoid_create_*` composite tools accept optional `severity`; path-hop `[type:task,severity:5]` resolves. PR #139 / DiVoid #1611. |
| 0.3.0 | 2026-05-30 | `divoid_unlink_nodes` tool added (PR #132 / DiVoid #934). |
| 0.2.0 | 2026-05-22 | Phase 2 composites: `divoid_create_task`, `divoid_create_documentation`, `divoid_create_session_log`. |
| 0.1.0 | 2026-05-20 | Phase 1: 5 core tools wrapping read paths. |

This table is carried over from node #829 with its original PR/DiVoid citations restored, and stops at 0.9.0 — it predates 0.10.0 and 0.11.0 (the version-single-source fix, DiVoid #13109). Bringing it current is a separate, unrequested piece of work; a follow-up append covers it.

**Already installed and want to upgrade?** Skip to the [Upgrading](#upgrading) section below.

---

## What you need before you start

- **Python 3.11 or newer.** Check by opening a terminal (Command Prompt or PowerShell on Windows, Terminal on Mac) and running `python --version`. If it prints `3.11.x` or higher, you are set. If it says `command not found` or shows an older version, install Python from [python.org/downloads](https://www.python.org/downloads/) — on Windows, tick **"Add Python to PATH"** in the installer.
- **An MCP host** — either **Claude Code** (CLI tool), **Claude Desktop** (Mac/Windows app), or another MCP-compatible host. The install steps below have one section per host.
- **A DiVoid API key.** Your operator (the human running the project) gives you this — it is a long opaque string. The key is **not** in the graph and is **not** shareable; each team member uses their own. If you do not have one yet, ask the operator before continuing.

---

## Step 1 — Install into a dedicated venv

Unlike a plain `pip install`, this creates one small, private Python environment just for divoid-mcp, at a fixed location: `~/.divoid-mcp/venv` (Windows: `%USERPROFILE%\.divoid-mcp\venv`). That location matters — Step 3 registers your MCP host against the **absolute path** of the script this venv produces, so the registration does not depend on which `python` happens to be first on your PATH, which broke down for real (DiVoid #13221).

**Mac/Linux:**

```bash
python -m venv ~/.divoid-mcp/venv
~/.divoid-mcp/venv/bin/python -m pip install "git+https://github.com/telmengedar/DiVoid.git#subdirectory=divoid-mcp"
```

**Windows (Command Prompt or PowerShell):**

```
python -m venv %USERPROFILE%\.divoid-mcp\venv
%USERPROFILE%\.divoid-mcp\venv\Scripts\python.exe -m pip install "git+https://github.com/telmengedar/DiVoid.git#subdirectory=divoid-mcp"
```

Measured cost from zero: about 20–25 seconds. You should see a couple of lines about resolving dependencies, then `Successfully installed divoid-mcp-0.x.0` (or a similar version number).

**If pip prints "WARNING: divoid-mcp.exe is not on PATH":** that is expected and safe to ignore here — we never rely on PATH to find this script. Step 3 registers its full path directly.

**If `python -m venv` says "command not found":** Python is not on your PATH. On Windows, reinstall Python and tick "Add Python to PATH". On Mac, use `python3 -m venv ...` instead.

**If you see a wall of red errors:** check Python version (`python --version`). If less than 3.11, install a newer Python first.

**Self-check.** Run the script the venv just produced, directly:

```
~/.divoid-mcp/venv/bin/divoid-mcp          # Windows: %USERPROFILE%\.divoid-mcp\venv\Scripts\divoid-mcp.exe
```

With no credentials configured yet, you should see two or three log lines on screen naming the missing `DIVOID_MCP_URL` / `DIVOID_MCP_API_KEY` variables, then the process exits — that is expected at this point and confirms the venv and the script both work.

**If you already have the deprecated fallback credentials file** (`~/.claude/secrets/.divoid-online`, see Step 2) from an earlier install, the script will instead start successfully and block, waiting on input — that is also a pass; press Ctrl+C to get your terminal back. A hang is only a problem if you have *neither* the fallback file *nor* any output at all (see "the install did not complete" below).

If instead you see `ModuleNotFoundError` or nothing at all, the install did not complete — re-run the two commands above.

---

## Step 2 — Get your DiVoid credentials ready

divoid-mcp takes its credentials from two environment variables, set in the `"env"` block of its entry in your MCP host's configuration — that is the MCP specification's own answer for stdio servers (see Architecture below). You do not create any file for this step; Step 3 shows the exact `env` block for each host.

- `DIVOID_MCP_URL` — the DiVoid API base URL, including the `/api` suffix: `https://divoid.mamgo.io/api`
- `DIVOID_MCP_API_KEY` — your DiVoid API key (your operator gives you this)

Keep your key handy; you will paste it into the `env` block in Step 3.

**Never paste the API key anywhere else** — not into chat messages, not into DiVoid nodes, not into git commits. If you suspect it leaked, ask the operator to rotate it. **`claude mcp get divoid` prints environment values in full plaintext**, so avoid running it in a shared or logged terminal once your key is configured.

### Deprecated fallback — existing installs only

If you already have a `~/.claude/secrets/.divoid-online` file from before this server read the environment, it keeps working: divoid-mcp falls back to it whenever neither `DIVOID_MCP_URL` nor `DIVOID_MCP_API_KEY` is set. Once either variable is set, both are required and this file is not consulted at all, even if it exists — see Troubleshooting if that surprises you. This fallback is deprecated and will be removed — startup logs a `WARNING` and the agent's own instructions carry a deprecation notice while you're running on it. Move your credentials into the `env` block in Step 3 when convenient; there is no urgency, but do it before you are asked to. The file format, if you still need it: a two-line `Url=...` / `ApiKey=...` text file at `~/.claude/secrets/.divoid-online` (Windows: `C:\Users\<you>\.claude\secrets\.divoid-online`). Node #829's older per-OS creation walkthrough for this file (the Notepad `.txt`-suffix trap, `ren`, `mkdir -p`) is deliberately not carried over here — it is a deprecated fallback now, not the primary path this doc walks you through, so a new install has no reason to create the file at all.

---

## Step 3 — Register the absolute path with your host

Every host below registers the **same value**: the absolute path to the `divoid-mcp` script inside the venv you just created. No interpreter name, no module name, no PATH lookup — one path, and it is the same path in every one of the three sections below.

- **Mac/Linux:** `/home/<you>/.divoid-mcp/venv/bin/divoid-mcp` (substitute your real home directory)
- **Windows:** `C:\Users\<you>\.divoid-mcp\venv\Scripts\divoid-mcp.exe` (substitute your real username)

**`~` must be expanded to the real path in JSON configuration files** — JSON does not expand `~` itself. A shell command line (like the `claude mcp add` line below) does expand `~`, so that one is safe to use verbatim.

### 3a — Claude Code

```
claude mcp add --transport stdio --scope user divoid \
  -e DIVOID_MCP_URL=https://divoid.mamgo.io/api -e DIVOID_MCP_API_KEY=<paste-your-api-key-here> \
  -- ~/.divoid-mcp/venv/bin/divoid-mcp
```

Windows: replace the last line with `-- %USERPROFILE%\.divoid-mcp\venv\Scripts\divoid-mcp.exe`.

The `--scope user` part makes the server available across every project on your machine, so you only do this once. The `-e` flags set the `env` block from Step 2. The `--` separates Claude Code's own flags from the command that runs the server.

Verify with:

```
claude mcp list
```

This is the safe command for this check: it prints only the server's name, command, and
connect status, never environment values — unlike `claude mcp get`, which does (see the
warning above). You should see a line like `divoid: /home/<you>/.divoid-mcp/venv/bin/divoid-mcp - Connected`. If it
says `Connected`, skip to **Step 4**. If it says `Failed to connect`, go to Troubleshooting.

### 3b — Claude Desktop

Claude Desktop reads its MCP configuration from a JSON file. The file location depends on your operating system:

- **Mac:** `~/Library/Application Support/Claude/claude_desktop_config.json`
- **Windows:** `%APPDATA%\Claude\claude_desktop_config.json` (typically `C:\Users\<you>\AppData\Roaming\Claude\claude_desktop_config.json`)

Open that file in any text editor. If it does not exist yet, create it with the content below. If it does exist and already has an `mcpServers` block, add the `divoid` key inside the existing block — do not overwrite what is already there.

```json
{
  "mcpServers": {
    "divoid": {
      "command": "/home/<you>/.divoid-mcp/venv/bin/divoid-mcp",
      "args": [],
      "env": {
        "DIVOID_MCP_URL": "https://divoid.mamgo.io/api",
        "DIVOID_MCP_API_KEY": "<paste-your-api-key-here>"
      }
    }
  }
}
```

Windows: `"command": "C:\\Users\\<you>\\.divoid-mcp\\venv\\Scripts\\divoid-mcp.exe"` (note the doubled backslashes — JSON escaping).

Replace `<paste-your-api-key-here>` with your actual key, and `/home/<you>/...` / `C:\Users\<you>\...` with your real home directory.

Save the file, then **fully quit Claude Desktop** (Cmd+Q on Mac, right-click tray icon → Quit on Windows — closing the window is not enough) and re-open it.

To verify: open a chat, ask Claude *"What MCP servers do you have available?"*. The response should mention `divoid` with several `divoid_*` tools.

### 3c — Any other MCP-compatible host (Cowork, generic clients)

If your host is something other than the two above, follow its documentation for adding a stdio-transport MCP server. The configuration values you need are:

- **Command:** the absolute path from the top of this step — `/home/<you>/.divoid-mcp/venv/bin/divoid-mcp` (Mac/Linux) or `C:\Users\<you>\.divoid-mcp\venv\Scripts\divoid-mcp.exe` (Windows)
- **Args:** none
- **Transport:** stdio (the server does not listen on a port; it talks over standard input/output)
- **Environment (required):** `DIVOID_MCP_URL=<the DiVoid API base URL, incl. /api>`, `DIVOID_MCP_API_KEY=<your key>`
- **Environment (optional):** `DIVOID_MCP_LOG_LEVEL=INFO` for default verbosity; set to `DEBUG` while troubleshooting

Most hosts that support MCP accept some flavour of the JSON config shown in section 3b. The exact path and surrounding keys vary; the command value does not.

---

## Step 4 — Verify it works

Open a new session in your MCP host (start a fresh chat in Claude Code, or open a new conversation in Claude Desktop). Then ask the agent:

> *"Use divoid_search to find the agent onboarding node, then use divoid_get_content to read its first 500 characters."*

If the agent comes back with content about "DiVoid — agent onboarding", the install is working end-to-end.

If the agent says it does not have a `divoid_search` tool, the host has not picked up the MCP server. Go back to Step 3 and re-check the registration, then fully quit and re-open the host.

---

## Troubleshooting

**We cannot make every MCP host display what the server printed on its error stream** — some hosts (Hermes among them) collapse any failed connection into a generic message like "Connection Closed" with no further detail. The sections below are ordered by what your host actually shows you, because that is what you have to go on.

### The host says the command was not found / no such file, or refuses to spawn it at all

The path in your registration is wrong, or the venv is gone (moved, renamed, deleted, or never created). This is the good failure mode — most hosts report it clearly and name the offending path. Re-do Step 1 (recreate the venv if needed) and Step 3 (re-check the exact path you registered), and make sure the backslashes/forward-slashes and `<you>` placeholder were actually replaced with your real path.

### The host says "Connection Closed" / "Failed to connect" and nothing else

**Copy the exact `command` value out of your host's configuration and run it yourself, verbatim, in a terminal.** Because the registration is now a single absolute path with nothing left to resolve, this reproduces exactly what the host tried to do — unlike running `python -m divoid_mcp` in your own shell, which uses your shell's PATH and Python and can succeed even when the host's own spawn fails.

Expected output if credentials are not yet configured: a couple of `INFO`/`ERROR` lines naming the missing `DIVOID_MCP_URL` / `DIVOID_MCP_API_KEY` variables, then the process exits (this is what the Step 1 self-check showed you). If instead you see **nothing at all** — no output, immediate exit — the venv has most likely been moved or renamed since you registered it: recreate it per Step 1, and re-register the new path if it changed.

- **`ModuleNotFoundError: No module named 'divoid_mcp'`** — the package is not installed in this venv. Re-run Step 1.
- **`No DiVoid credentials found -- divoid-mcp cannot start.`** — neither `DIVOID_MCP_URL`/`DIVOID_MCP_API_KEY` nor the fallback file is set up. Re-check Step 2 and the `env` block in Step 3.
- **`DIVOID_MCP_API_KEY is set but DIVOID_MCP_URL is empty or missing`** — you set the key but not the URL in the `env` block; both are required together. Re-check Step 3.
- **`DIVOID_MCP_URL is set but DIVOID_MCP_API_KEY is empty or missing`** — you set the URL but not the key in the `env` block. The fallback file is deliberately **not** consulted here, even if it exists — using its key with the URL you just set could send that key to a different DiVoid instance than the one the file was written for. Re-check that the key is spelled exactly `DIVOID_MCP_API_KEY` in the same `env` block.
- **The fallback credentials file is malformed / has an empty value** — if you're on the deprecated file fallback, re-check its `Url=` / `ApiKey=` lines have no typos and no empty values.

### `401 Unauthorized` errors when the agent uses the tools

The API key is wrong or expired. Check the credentials this server was started with — the startup log line `Config loaded: source=...` names which source was used (`env`, or `file:<path>` for the deprecated fallback). Re-check `DIVOID_MCP_API_KEY` in your host's `env` block, or the `ApiKey=` line in `~/.claude/secrets/.divoid-online` if you're on the fallback. If the key was rotated by the operator, get the new one and replace it.

### `data_entitynotfound` on every call

Usually the `Url=` line (or `DIVOID_MCP_URL` value) is missing the `/api` suffix — the server is hitting `divoid.mamgo.io` instead of `divoid.mamgo.io/api/nodes/<id>/content`. Re-check Step 2.

### Drift-canary `MISMATCH` warning on startup

```
WARNING divoid_mcp.drift: API reference hash MISMATCH — node #8 content has changed ...
```

This is **not fatal** — the server keeps running, all tools work. It means DiVoid's API reference node has been updated since this version of divoid-mcp was last released. A future divoid-mcp release will bump the pinned hash. No action needed on your side.

### `FastMCP.__init__() got an unexpected keyword argument 'version'`

You are running a pre-fix version (older than 2026-05-22). Upgrade per [Upgrading](#upgrading) below.

### Tools missing parameters you expect (e.g. no `severity_min` on `divoid_list`)

Your local install predates the feature. Upgrade per [Upgrading](#upgrading) below, then fully restart your MCP host so the new tool schemas load.

### Known pip-metadata-vs-code skew

`pip show divoid-mcp` can report an older version than the code actually installed is — this was tracked as DiVoid #1638 and is now **closed** (the version now derives from a single source, `divoid_mcp/version.py`, per DiVoid #13109 / PR #187). If you still see a mismatch on an existing install, the symptom guidance stands even though the underlying task is closed — treat `python -c "from divoid_mcp.version import __version__; print(__version__)"` (or, in the venv from Step 1, `~/.divoid-mcp/venv/bin/python -c "..."`) as the trustworthy check, not `pip show`. If you're setting up fresh via Step 1, you should not hit this at all.

### macOS, multi-account machine, or Homebrew Python fails to `pip install`

This is a named **exception** to the blessed venv path above, not a peer option — use it only if a plain `pip install` (Step 1) genuinely fails on your machine for one of these reasons:

- **libexpat symbol mismatch (Homebrew Python 3.14).** Homebrew's Python 3.14 can ship with a libexpat symbol mismatch — `pyexpat: Symbol not found: _XML_SetAllocTrackerActivationThreshold` — which kills **every** `pip install` (pip imports `pyexpat` on startup). Do not fight pip; use the `uv`-based install below, which brings its own Python.
- **Shared `/opt/homebrew` on multi-account Macs.** On a Mac with more than one user account, `/opt/homebrew` is often owned by a *different* user, so `pip`/`brew` writes fail with permission errors for the current account. **Do NOT `chown` it to take ownership** — that locks the other account out of Homebrew. Bypass Homebrew entirely instead (per account).

Recommended per-account install in that situation, bypassing Homebrew (uses `uv`):

```
uv python install 3.12
uv tool install --python 3.12 "git+https://github.com/telmengedar/DiVoid.git#subdirectory=divoid-mcp"
```

This installs an isolated Python 3.12 and the divoid-mcp tool under the current account's own `~/.local`, with no Homebrew and no shared-path writes. `uv` places the launcher at `~/.local/bin/divoid-mcp` — register **that** absolute path instead of the `~/.divoid-mcp/venv/...` one above; everything else in Step 3 (the `env` block, the JSON shape) is unchanged. The Claude CLI may be app-embedded and not on your PATH in this setup, so `claude mcp add` might not be runnable — in that case edit `~/.claude.json` directly (e.g. via a `jq` edit) to add the server:

```json
{
  "mcpServers": {
    "divoid": {
      "command": "/Users/<you>/.local/bin/divoid-mcp"
    }
  }
}
```

Use the absolute path — `~` may not expand inside the JSON. Restart the host afterwards so it loads the server.

### Claude Desktop does not see `divoid_*` tools

- Did you fully quit Claude Desktop (not just close the window) and re-open it? MCP servers are loaded at app startup only.
- Is the JSON in `claude_desktop_config.json` valid? A trailing comma or missing brace will silently disable the whole file. Paste it into [jsonlint.com](https://jsonlint.com) to check.
- Is the `"command"` value the real absolute path from Step 1, with `<you>` actually replaced? Test it in a terminal directly (see "Connection Closed" above) — if that errors, the same value will not work in Claude Desktop.

---

## Upgrading

A divoid-mcp release announcement on the **DiVoid Tasks** group (or a `[DiVoid] divoid-mcp X.Y.Z released` message in your inbox) is your trigger to upgrade. The MCP server is **per-machine, per-session-loaded** — installing a new pip version does not affect a running session; you must restart the MCP host afterwards.

### Upgrade command

```
~/.divoid-mcp/venv/bin/python -m pip install --upgrade --force-reinstall "git+https://github.com/telmengedar/DiVoid.git#subdirectory=divoid-mcp"
```

Windows: `%USERPROFILE%\.divoid-mcp\venv\Scripts\python.exe -m pip install --upgrade --force-reinstall "git+https://github.com/telmengedar/DiVoid.git#subdirectory=divoid-mcp"`.

The `--force-reinstall` flag is the safe default — `--upgrade` alone sometimes treats a stale local cache as "already latest" and skips. After the install completes, **fully quit your MCP host** (Claude Code: exit the CLI; Claude Desktop: Cmd+Q on Mac, right-click tray → Quit on Windows) and re-open it. The host re-loads the MCP server with the new tool schemas on startup. The registered path does not change across an upgrade — the venv is reused in place.

### If the upgrade fails with `WinError 5 / Zugriff verweigert` on a `.pyd`

On a machine where **other MCP sessions are running right now**, `--force-reinstall` reinstalls every dependency too — and a running server holds its compiled extensions open. The install then dies partway with something like:

```
ERROR: Could not install packages due to an OSError: [WinError 5] Zugriff verweigert:
'...\site-packages\rpds\rpds.cp314-win_amd64.pyd'
```

**The lock is information, not an obstacle: another session is using that file.** Do not kill the process, do not close someone else's host, and do not retry with elevation.

Add `--no-deps`:

```
~/.divoid-mcp/venv/bin/python -m pip install --upgrade --force-reinstall --no-deps "git+https://github.com/telmengedar/DiVoid.git#subdirectory=divoid-mcp"
```

This replaces only `divoid_mcp` itself, whose `.py` files are not held open the way a `.pyd` is. **Check first that the release actually changed dependencies** — compare `pyproject.toml`'s `dependencies` against what you have installed. If they are unchanged (the common case for a code-only release), `--no-deps` is exactly right. If a release *does* add or bump a dependency, `--no-deps` will silently leave you on the old one, so in that case wait until the other sessions are closed and run the full command.

Observed 2026-09-01 deploying 0.9.0 with three sessions live.

### Verify the upgrade

```
~/.divoid-mcp/venv/bin/python -c "from divoid_mcp.version import __version__; print(__version__)"
```

This should print the latest version (see Recent releases above). Then start a fresh chat and ask the agent which divoid-mcp version it's running, or call a tool that uses a newly-added parameter.

### After upgrade — drift-canary warning

See "Drift-canary `MISMATCH` warning on startup" under Troubleshooting above — it is not fatal.

---

## Uninstall

If you ever need to remove the divoid-mcp server:

**Claude Code:**

```
claude mcp remove divoid -s user
```

**Claude Desktop:** remove the `divoid` key from your `claude_desktop_config.json`, then restart Claude Desktop.

**Either host:** delete the venv — `rm -rf ~/.divoid-mcp` (Mac/Linux) or delete `%USERPROFILE%\.divoid-mcp` in File Explorer (Windows). Nothing else on your machine references it.

---

## Updating the install doc

If you ran into a quirk that this doc did not cover — Windows path, Mac SIP weirdness, a specific MCP host's config format, a Python version that needed a workaround — please update this doc with what you learned. It is a wiki-style document on DiVoid (node **#829**, mirroring this file), and the next team member onboarding will benefit from your fix. Either edit directly (if you have write access) or ask your agent to file a task on the DiVoid Tasks group (#314) with the diff to apply.

---

## Architecture (for the curious)

The full architecture document is at `docs/architecture/phase-1.md` in the divoid-mcp repo and DiVoid node **#695**. The server is a thin Python wrapper around the DiVoid REST API; it adds no state, no caching beyond a startup drift-canary, and no retries.

The server exposes **22 tools** as of 0.11.0. This document deliberately does not reproduce the list — a second copy drifts, and it did: this line carried 17 names for months after `divoid_create_node`, `divoid_delete_node`, `divoid_download_content`, `divoid_edit_content` and `divoid_patch_link` had shipped. **The authoritative surface is whatever your harness lists**, and the annotated per-module breakdown lives at DiVoid **#5985**. There are also MCP resources for the canonical reference documents (nodes #9, #190, #8, #493, #435).
