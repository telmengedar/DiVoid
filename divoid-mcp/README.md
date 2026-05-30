# divoid-mcp

MCP server wrapping the [DiVoid](https://divoid.mamgo.io) graph API. Since 2026-05-22 this server is the **canonical interface** for agents interacting with DiVoid — prefer the `divoid_*` tools over raw `curl`. See [DiVoid node #190 § Tooling](https://divoid.mamgo.io/api/nodes/190/content) for the full policy; the short version is: use MCP by default, fall back to REST only when the server isn't available or a tool misbehaves, and file a DiVoid task when you do fall back.

## User install

Full step-by-step guide (non-technical friendly, covers Claude Code, Claude Desktop, and generic MCP hosts): [`docs/install.md`](docs/install.md) / DiVoid node [**#829**](https://divoid.mamgo.io/api/nodes/829/content).

Quick path for experienced users:

```
pip install "git+https://github.com/telmengedar/DiVoid.git#subdirectory=divoid-mcp"
claude mcp add --transport stdio --scope user divoid -- python -m divoid_mcp
```

## Tools (16)

| Tool | What it does |
|---|---|
| `divoid_search` | Semantic search over the graph — returns nodes ranked by cosine similarity; supports timestamp range filters (created_from/to, updated_from/to) |
| `divoid_get_node` | Fetch a single node's metadata (id, type, name, status, access, ownerId, created, lastUpdate) by id |
| `divoid_get_content` | Fetch the text body of a node — decoded as UTF-8 |
| `divoid_list` | List nodes with filtering by type, status, linkedto, name, id, and timestamp ranges (created_from/to, updated_from/to); returns paged results |
| `divoid_get_links` | Return all nodes linked to a given node (one-hop neighbours) |
| `divoid_link_nodes` | Create an undirected link between two existing nodes |
| `divoid_unlink_nodes` | Remove an undirected link between two existing nodes; idempotent |
| `divoid_patch_node` | Apply JSON-Patch operations to a node's metadata fields (name, status, x, y, access, owner_id) |
| `divoid_set_status` | Set or clear a node's status field — enforces valid lifecycle values client-side |
| `divoid_set_content` | Post content to a node's body — UTF-8 safe, no bash heredoc mangling |
| `divoid_create_task` | Atomic create: makes the node, sets its content, links it to the project's Tasks group; accepts optional `access` param |
| `divoid_create_documentation` | Atomic create: makes the node, sets its content, links it to the project's Docs group; accepts optional `access` param |
| `divoid_create_session_log` | Atomic create: makes the node, sets its content, links it to the project's Docs group + any extra links; accepts optional `access` param |
| `divoid_resolve_user` | Look up a DiVoid user by name — returns the user id needed for message routing |
| `divoid_send_message` | Send a message to a DiVoid user's inbox |
| `divoid_list_messages` | List messages in a user's inbox, optionally filtered by project |

Five MCP resources are also exposed for the canonical DiVoid reference documents: nodes #9 (onboarding), #190 (Hivemind Protocol), #8 (API reference), #493 (structural conventions), #435 (messaging system).

## Prerequisites

- Python 3.11+
- A DiVoid API key in `~/.claude/secrets/.divoid-online` (two-line `Url=...` / `ApiKey=...` format)

## Configuration

The server reads `~/.claude/secrets/.divoid-online` at startup. This file must exist and contain:

```
Url=https://divoid.mamgo.io/api
ApiKey=<your-key>
```

The API key **never** appears in tool parameters, error messages, or logs. The file path may appear in error messages.

**Log level** is controlled via `DIVOID_MCP_LOG_LEVEL` (default `INFO`). Valid values: `DEBUG`, `INFO`, `WARNING`, `ERROR`. All logs go to **stderr** (stdout carries the JSON-RPC stream).

## Smoke tests

Run the live smoke suite against the real DiVoid instance — each tool is called once and the response shape is validated:

```bash
pip install -e .
python tests/smoke/run_all.py
```

Results print as `PASS` / `FAIL` with details. Requires `~/.claude/secrets/.divoid-online` with valid credentials. See `tests/smoke/README.md` for the full assertion table.

**Hermetic unit tests** pin the tool routing logic without network calls:

```bash
pip install -e ".[dev]"
pytest tests/unit/ -v
```

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

