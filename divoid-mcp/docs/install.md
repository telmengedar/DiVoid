# divoid-mcp: install in 5 minutes

Step-by-step install guide for team members and the orchestrator's future self.
Tested on Windows 11 with Python 3.14.2.

---

## Prerequisites

- **Python 3.11+** (tested with 3.14.2 on Windows 11; no compatibility issues observed at install time).
  Install from [python.org](https://www.python.org/downloads/) — the Windows installer adds Python to PATH by default.
- **pip** — ships with Python.
- **Claude Code** installed and working (or another MCP host that supports stdio transport).
- **A DiVoid API key** — each team member uses their own personal key.
  You already have one if you currently call the DiVoid REST API directly.

---

## Step 1 — install the package

```
pip install "git+https://github.com/telmengedar/DiVoid.git#subdirectory=divoid-mcp"
```

pip may warn that the `divoid-mcp.exe` script landed in a user Scripts directory not on PATH.
**This is fine** — we invoke the server as `python -m divoid_mcp`, which does not need the
script on PATH.

---

## Step 2 — create the secrets file

Create `~/.claude/secrets/.divoid-online` with exactly two lines:

```
Url=https://divoid.mamgo.io/api
ApiKey=<your-divoid-api-key>
```

The trailing `/api` on the URL matters — the divoid-mcp HTTP client appends paths to this prefix.
Do not put credentials in any other file; this location is what the server looks for.

---

## Step 3 — register the MCP server at user scope

```
claude mcp add --transport stdio --scope user divoid -- python -m divoid_mcp
```

`--scope user` makes the server available across all your projects (not just one repo).
The `--` separates Claude Code flags from the command that launches the server — everything
after it is the executable and its arguments.

---

## Step 4 — restart Claude Code

MCP servers are loaded at startup.
Close Claude Code entirely and reopen it, or run `/reload` inside an existing session.

---

## Step 5 — verify the connection

```
claude mcp list
```

Expected output includes:

```
divoid: python -m divoid_mcp - Connected
```

If you see `Connected`, you are done. The server exposes 15 `divoid_*` tools and 5
`divoid://node/*` resources that Claude Code's tool browser will list.

---

## Step 6 — smoke check in Claude Code

Open a new Claude Code session and ask the agent to run `divoid_search` for a known topic,
for example: *"Use divoid_search to find the agent onboarding node."*

If the tool returns results, the install is working end-to-end.

---

## Troubleshooting

### `Failed to connect` in `claude mcp list`

The server failed to start. Run it directly to see the error:

```
python -m divoid_mcp < /dev/null
```

The first few seconds of stderr output will name the exact failure.
Common causes are listed below.

---

### `401 Unauthorized` (or `data_entitynotfound` on every call)

The API key is wrong or expired.
Re-check the `ApiKey=` line in `~/.claude/secrets/.divoid-online` against your current key.

`data_entitynotfound` errors on the startup canary (node #8) usually mean the `Url=` line is
missing the `/api` suffix — the server is hitting `divoid.mamgo.io` instead of
`divoid.mamgo.io/api/nodes/8/content`.

---

### Drift-canary `MISMATCH` warning on startup

```
WARNING divoid_mcp.drift: API reference hash MISMATCH — node #8 content has changed ...
```

This is a **warning, not a fatal error** — the server continues and all tools work.
It means DiVoid's API reference node (#8) has been updated since this version of divoid-mcp
was pinned. See `docs/drift-policy.md` for how to update the pin.
DiVoid task #817 tracks the next scheduled pin-bump.

---

### `FastMCP.__init__() got an unexpected keyword argument 'version'`

This was the bug fixed in this PR. If you see it, you are running a pre-fix version of
divoid-mcp. Upgrade:

```
pip install --upgrade "git+https://github.com/telmengedar/DiVoid.git#subdirectory=divoid-mcp"
```

---

## Uninstall

```
claude mcp remove divoid -s user
pip uninstall divoid-mcp
```

---

## Architecture

The full architecture document is at `docs/architecture/phase-1.md` and DiVoid node **#695**.
