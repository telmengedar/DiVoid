# divoid-mcp: install in 5 minutes

The MCP server that gives your agent the `divoid_*` tools — `divoid_search`, `divoid_get_node`, `divoid_get_content`, `divoid_create_task`, `divoid_send_message`, and ten more. After install, your agent stops calling DiVoid via raw `curl` and starts using these tools, which means UTF-8 works, invariants are checked client-side, and responses come back as structured JSON instead of curl output.

This doc walks **non-technical team members** through the install end-to-end. If you have used the DiVoid REST API once, you already have everything you need. If you have not — you only need an API key (your operator gives you that) and the right MCP host installed (Claude Code, Claude Desktop, or another MCP-compatible tool). The full install takes about five minutes per host once Python is present.

If your agent is the one reading this — you can install it for the human in front of you. Walk them through each step, run the verification command in the last section, and you are done. The team-member-facing language is intentional; the same steps work for you as a subroutine.

---

## What you need before you start

- **Python 3.11 or newer.** Check by opening a terminal (Command Prompt or PowerShell on Windows, Terminal on Mac) and running `python --version`. If it prints `3.11.x` or higher, you are set. If it says `command not found` or shows an older version, install Python from [python.org/downloads](https://www.python.org/downloads/) — on Windows, tick **"Add Python to PATH"** in the installer.
- **An MCP host** — either **Claude Code** (CLI tool), **Claude Desktop** (Mac/Windows app), or another MCP-compatible host. The install steps below have one section per host.
- **A DiVoid API key.** Your operator (the human running the project) gives you this — it is a long opaque string. The key is **not** in the graph and is **not** shareable; each team member uses their own. If you do not have one yet, ask the operator before continuing.

---

## Step 1 — Install the divoid-mcp Python package

This step is identical regardless of which MCP host you use. Open a terminal and run:

```
pip install "git+https://github.com/telmengedar/DiVoid.git#subdirectory=divoid-mcp"
```

That is one line — copy it verbatim. The `pip` command downloads the package directly from GitHub. You should see a couple of lines about resolving dependencies, then `Successfully installed divoid-mcp-0.1.0` (or a similar version number).

**If pip says "command not found":** Python is not on your PATH. On Windows, reinstall Python and tick "Add Python to PATH". On Mac, run `python3 -m pip install "git+https://github.com/telmengedar/DiVoid.git#subdirectory=divoid-mcp"` instead — same command, prefixed with `python3 -m`.

**If pip prints "WARNING: divoid-mcp.exe is not on PATH":** ignore it. We do not need the `divoid-mcp` script to be on PATH; we run the server as `python -m divoid_mcp` instead, which always works.

**If you see a wall of red errors:** check Python version (`python --version`). If less than 3.11, install a newer Python first.

---

## Step 2 — Get your DiVoid credentials ready

divoid-mcp takes its credentials from two environment variables, set in the `"env"` block of its entry in your MCP host's configuration — that is the MCP specification's own answer for stdio servers (see Architecture below). You do not create any file for this step; Step 3 shows the exact `env` block for each host.

- `DIVOID_MCP_URL` — the DiVoid API base URL, including the `/api` suffix: `https://divoid.mamgo.io/api`
- `DIVOID_MCP_API_KEY` — your DiVoid API key (your operator gives you this)

Keep your key handy; you will paste it into the `env` block in Step 3.

**Never paste the API key anywhere else** — not into chat messages, not into DiVoid nodes, not into git commits. If you suspect it leaked, ask the operator to rotate it. **`claude mcp get divoid` prints environment values in full plaintext**, so avoid running it in a shared or logged terminal once your key is configured.

### Deprecated fallback — existing installs only

If you already have a `~/.claude/secrets/.divoid-online` file from before this server read the environment, it keeps working: divoid-mcp falls back to it whenever neither `DIVOID_MCP_URL` nor `DIVOID_MCP_API_KEY` is set. Once either variable is set, both are required and this file is not consulted at all, even if it exists — see Troubleshooting if that surprises you. This fallback is deprecated and will be removed — startup logs a `WARNING` and the agent's own instructions carry a deprecation notice while you're running on it. Move your credentials into the `env` block in Step 3 when convenient; there is no urgency, but do it before you are asked to. The file format, if you still need it: a two-line `Url=...` / `ApiKey=...` text file at `~/.claude/secrets/.divoid-online` (Windows: `C:\Users\<you>\.claude\secrets\.divoid-online`).

---

## Step 3 — Register the MCP server with your host

Pick the section that matches the MCP host you use.

### 3a — Claude Code

Claude Code has a built-in command for registering MCP servers. Run (replacing `<paste-your-api-key-here>` with your actual key):

```
claude mcp add --transport stdio --scope user divoid \
  -e DIVOID_MCP_URL=https://divoid.mamgo.io/api -e DIVOID_MCP_API_KEY=<paste-your-api-key-here> \
  -- python -m divoid_mcp
```

The `--scope user` part makes the server available across every project on your machine, so you only do this once. The `-e` flags set the `env` block from Step 2. The `--` separates Claude Code's own flags from the command that runs the server.

Verify with:

```
claude mcp list
```

This is the safe command for this check: it prints only the server's name, command, and
connect status, never environment values — unlike `claude mcp get`, which does (see the
warning above). You should see a line like `divoid: python -m divoid_mcp - Connected`. If it
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
      "command": "python",
      "args": ["-m", "divoid_mcp"],
      "env": {
        "DIVOID_MCP_URL": "https://divoid.mamgo.io/api",
        "DIVOID_MCP_API_KEY": "<paste-your-api-key-here>"
      }
    }
  }
}
```

Replace `<paste-your-api-key-here>` with your actual key.

**On Windows specifically:** `python` may not be the right command name; check by running `python --version` in Command Prompt. If that errors, try `py --version`. Use whichever works as the `"command"` value in the JSON.

**On Mac specifically:** `python` on Mac may still point to Python 2.x for legacy reasons. Use the explicit `python3` (or the full path that `which python3` prints) as the `"command"` value.

Save the file, then **fully quit Claude Desktop** (Cmd+Q on Mac, right-click tray icon → Quit on Windows — closing the window is not enough) and re-open it.

To verify: open a chat, ask Claude *"What MCP servers do you have available?"*. The response should mention `divoid` with several `divoid_*` tools.

### 3c — Any other MCP-compatible host (Cowork, generic clients)

If your host is something other than the two above, follow its documentation for adding a stdio-transport MCP server. The configuration values you need are:

- **Command:** `python` (or `python3` on Mac if `python` points to Python 2.x)
- **Args:** `-m divoid_mcp`
- **Transport:** stdio (the server does not listen on a port; it talks over standard input/output)
- **Environment (required):** `DIVOID_MCP_URL=<the DiVoid API base URL, incl. /api>`, `DIVOID_MCP_API_KEY=<your key>`
- **Environment (optional):** `DIVOID_MCP_LOG_LEVEL=INFO` for default verbosity; set to `DEBUG` while troubleshooting

Most hosts that support MCP accept some flavour of the JSON config shown in section 3b. The exact path and surrounding keys vary; the server-spec values do not.

---

## Step 4 — Verify it works

Open a new session in your MCP host (start a fresh chat in Claude Code, or open a new conversation in Claude Desktop). Then ask the agent:

> *"Use divoid_search to find the agent onboarding node, then use divoid_get_content to read its first 500 characters."*

If the agent comes back with content about "DiVoid — agent onboarding", the install is working end-to-end.

If the agent says it does not have a `divoid_search` tool, the host has not picked up the MCP server. Go back to Step 3 and re-check the registration, then fully quit and re-open the host.

---

## Troubleshooting

### `claude mcp list` shows `Failed to connect` (Claude Code)

The server crashed on startup. Run it directly to see the error message:

```
python -m divoid_mcp
```

The server should print a couple of startup log lines, then hang waiting for input (that is normal — kill it with Ctrl+C). If instead it prints an error and exits immediately, the message tells you what went wrong. The most common causes:

- **`ModuleNotFoundError: No module named 'divoid_mcp'`** — the package is not installed in this Python. Re-run Step 1.
- **`No DiVoid credentials found -- divoid-mcp cannot start.`** — neither `DIVOID_MCP_URL`/`DIVOID_MCP_API_KEY` nor the fallback file is set up. Re-check Step 2 and the `env` block in Step 3.
- **`DIVOID_MCP_API_KEY is set but DIVOID_MCP_URL is empty or missing`** — you set the key but not the URL in the `env` block; both are required together. Re-check Step 3.
- **`DIVOID_MCP_URL is set but DIVOID_MCP_API_KEY is empty or missing`** — you set the URL but not the key in the `env` block. The fallback file is deliberately **not** consulted here, even if it exists — using its key with the URL you just set could send that key to a different DiVoid instance than the one the file was written for. Re-check that the key is spelled exactly `DIVOID_MCP_API_KEY` in the same `env` block.
- **The fallback credentials file is malformed / has an empty value** — if you're on the deprecated file fallback, re-check its `Url=` / `ApiKey=` lines have no typos and no empty values.

### Claude Desktop does not see `divoid_*` tools

- Did you fully quit Claude Desktop (not just close the window) and re-open it? MCP servers are loaded at app startup only.
- Is the JSON in `claude_desktop_config.json` valid? A trailing comma or missing brace will silently disable the whole file. Paste it into [jsonlint.com](https://jsonlint.com) to check.
- Is the `"command"` value an executable that exists on your PATH? Test in a terminal: `python -m divoid_mcp`. If that errors, the same value will not work in Claude Desktop. Try `python3` or the full path printed by `which python3` (Mac) / `where python` (Windows).

### `401 Unauthorized` errors when the agent uses the tools

The API key is wrong or expired. Check the credentials this server was started with — the startup log line `Config loaded: source=...` names which source was used (`env`, or `file:<path>` for the deprecated fallback). Re-check `DIVOID_MCP_API_KEY` in your host's `env` block, or the `ApiKey=` line in `~/.claude/secrets/.divoid-online` if you're on the fallback. If the key was rotated by the operator, get the new one and replace it.

### `data_entitynotfound` on every call

Usually the `Url=` line is missing the `/api` suffix — the server is hitting `divoid.mamgo.io` instead of `divoid.mamgo.io/api/nodes/<id>/content`. Re-check Step 2.

### Drift-canary `MISMATCH` warning on startup

```
WARNING divoid_mcp.drift: API reference hash MISMATCH — node #8 content has changed ...
```

This is **not fatal** — the server keeps running, all tools work. It means DiVoid's API reference node has been updated since this version of divoid-mcp was last released. A future divoid-mcp release will bump the pinned hash. No action needed on your side.

### `FastMCP.__init__() got an unexpected keyword argument 'version'`

You are running a pre-fix version (older than 2026-05-22). Upgrade:

```
pip install --upgrade "git+https://github.com/telmengedar/DiVoid.git#subdirectory=divoid-mcp"
```

Then re-do Step 3 (or restart your MCP host so it picks up the upgraded binary).

---

## Uninstall

If you ever need to remove the divoid-mcp server:

**Claude Code:**

```
claude mcp remove divoid -s user
pip uninstall divoid-mcp
```

**Claude Desktop:** remove the `divoid` key from your `claude_desktop_config.json`, then `pip uninstall divoid-mcp`. Restart Claude Desktop.

---

## Updating the install doc

If you ran into a quirk that this doc did not cover — Windows path, Mac SIP weirdness, a specific MCP host's config format, a Python version that needed a workaround — please update this doc with what you learned. It is a wiki-style document on DiVoid (node **#829**), and the next team member onboarding will benefit from your fix. Either edit directly (if you have write access) or ask your agent to file a task on the DiVoid Tasks group (#314) with the diff to apply.

---

## Architecture (for the curious)

The full architecture document is at `docs/architecture/phase-1.md` in the divoid-mcp repo and DiVoid node **#695**. The server is a thin Python wrapper around the DiVoid REST API; it adds no state, no caching beyond a startup drift-canary, and no retries.

The list of tools exposed: `divoid_search`, `divoid_get_node`, `divoid_get_content`, `divoid_list`, `divoid_get_links`, `divoid_link_nodes`, `divoid_patch_node`, `divoid_set_status`, `divoid_set_content`, `divoid_create_task`, `divoid_create_documentation`, `divoid_create_session_log`, `divoid_resolve_user`, `divoid_send_message`, `divoid_list_messages`. Plus five MCP resources for the canonical reference documents (nodes #9, #190, #8, #493, #435).
