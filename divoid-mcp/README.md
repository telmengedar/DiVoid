# divoid-mcp

MCP server wrapping the [DiVoid](https://divoid.mamgo.io) graph API. Exposes DiVoid as a Model Context Protocol server so Claude Code agents can read, search, and write to the graph without raw `curl` plumbing.

## What it does

- **`divoid_search`** — semantic search over the graph (question-style queries)
- **`divoid_get_node`** — fetch a single node's properties by id
- **`divoid_get_content`** — fetch the text body of a node
- **`divoid_link_nodes`** — create a link between two existing nodes
- **MCP resources** — live content of the five canonical DiVoid reference docs (nodes #9, #190, #8, #493, #435)

Phase 2 will add composite create tools (`divoid_create_task`, `divoid_create_documentation`) and mutation tools.

## Prerequisites

- Python 3.11+
- A DiVoid admin API key in `~/.claude/secrets/.divoid-online` (two-line `Url=...` / `ApiKey=...` format)

## Install

```bash
cd C:\dev\claude\divoid-mcp
pip install -e .
```

Or without an editable install:

```bash
pip install .
```

## Register in Claude Code

Copy `examples/.mcp.json` to your project's `.mcp.json`, or add the `divoid` server entry to your existing `.mcp.json`:

```json
{
  "mcpServers": {
    "divoid": {
      "command": "divoid-mcp",
      "args": [],
      "env": {
        "DIVOID_MCP_LOG_LEVEL": "INFO"
      }
    }
  }
}
```

If `divoid-mcp` is not on `PATH` (no `pip install` or not activated venv), use the module form:

```json
{
  "mcpServers": {
    "divoid": {
      "command": "python",
      "args": ["-m", "divoid_mcp"],
      "env": {
        "DIVOID_MCP_LOG_LEVEL": "INFO"
      }
    }
  }
}
```

See `examples/.mcp.json` for both forms with comments.

## Configuration

The server reads `~/.claude/secrets/.divoid-online` at startup. This file must exist and contain:

```
Url=https://divoid.mamgo.io/api
ApiKey=<your-key>
```

The API key **never** appears in tool parameters, error messages, or logs. The file path may appear in error messages.

**Log level** is controlled via `DIVOID_MCP_LOG_LEVEL` (default `INFO`). Valid values: `DEBUG`, `INFO`, `WARNING`, `ERROR`. All logs go to **stderr** (stdout carries the JSON-RPC stream).

## Smoke tests

Run against the live DiVoid instance:

```bash
cd C:\dev\claude\divoid-mcp
pip install -e .
python tests/smoke/run_all.py
```

Each tool is called once and the response shape is validated. Results are printed as `PASS` / `FAIL` with details. Requires the `~/.claude/secrets/.divoid-online` credential file to be present and valid.

See `tests/smoke/README.md` for details on what each test asserts.

## Architecture

Full architecture document: `docs/architecture/phase-1.md` and DiVoid node **#695**.

Key decisions:
- **stdio transport only** — no listening port, no HTTP server mode
- **No retries** — tools that are non-idempotent (create) must not be retried blindly; the caller decides
- **No caching** — every call goes to DiVoid live; the one exception is the startup drift-canary snapshot of node #8
- **UTF-8 safety** — content is posted as `bytes` via httpx, no shell interpolation
- **Fail-closed auth** — if the secret file is absent or malformed, the server exits non-zero immediately

## API drift canary

On startup the server fetches the health endpoint and computes a SHA-256 of node #8's content, comparing it against a constant pinned in `src/divoid_mcp/version.py`. A mismatch logs a `WARNING` but does not block startup. See `docs/drift-policy.md` for the update procedure.

## Phase 2 (planned)

- `divoid_create_task` — atomic create + content + Tasks-group link
- `divoid_create_documentation` — atomic create + content + Docs-group link
- `divoid_list` — path-query traversal
- `divoid_patch_node`, `divoid_set_status`, `divoid_set_content`
- `divoid_send_message`, `divoid_list_messages`
- `divoid_create_session_log`, `divoid_resolve_user`, `divoid_get_links`
