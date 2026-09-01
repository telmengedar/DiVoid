# divoid-mcp

MCP server wrapping the [DiVoid](https://divoid.mamgo.io) graph API. Since 2026-05-22 this server is the **canonical interface** for agents interacting with DiVoid — prefer the `divoid_*` tools over raw `curl`. See [DiVoid node #190 § Tooling](https://divoid.mamgo.io/api/nodes/190/content) for the full policy; the short version is: use MCP by default, fall back to REST only when the server isn't available or a tool misbehaves, and file a DiVoid task when you do fall back.

## User install

Full step-by-step guide (non-technical friendly, covers Claude Code, Claude Desktop, and generic MCP hosts): [`docs/install.md`](docs/install.md) / DiVoid node [**#829**](https://divoid.mamgo.io/api/nodes/829/content).

Quick path for experienced users:

```
pip install "git+https://github.com/telmengedar/DiVoid.git#subdirectory=divoid-mcp"
claude mcp add --transport stdio --scope user divoid -- python -m divoid_mcp
```

## Tools (22)

| Tool | What it does |
|---|---|
| `divoid_search` | Semantic search over the graph — returns nodes ranked by cosine similarity; supports timestamp range filters (created_from/to, updated_from/to); optional `include_links` / `include_link_details` surface inline adjacency (ids, or enriched `source_id`/`target_id`/`link_type`/`context` rows) |
| `divoid_get_node` | Fetch a single node's metadata (id, type, name, status, access, ownerId, created, lastUpdate) by id |
| `divoid_get_content` | Fetch the text body of a node — decoded as UTF-8 |
| `divoid_list` | List nodes with filtering by type, status, linkedto, name, id, and timestamp ranges (created_from/to, updated_from/to); returns paged results; optional `include_links` / `include_link_details` surface inline adjacency (ids, or enriched `source_id`/`target_id`/`link_type`/`context` rows) |
| `divoid_get_links` | Return link adjacency rows incident to a set of node ids, incl. `link_type` and `context` when the backend carries them |
| `divoid_download_content` | Fetch a node's content and write the raw bytes to a local file; path must resolve inside the server's configured workspace root(s) |
| `divoid_link_nodes` | Create a link between two existing nodes (undirected by default); optional `link_type` (Unidirectional/Bidirectional) and `context` (free text) |
| `divoid_unlink_nodes` | Remove an undirected link between two existing nodes; idempotent |
| `divoid_patch_node` | Apply JSON-Patch operations to a node's metadata fields (name, status, x, y, access, owner_id) |
| `divoid_patch_link` | Edit an existing link's `link_type` and/or `context` in place; missing edge is a hard 404 |
| `divoid_set_status` | Set or clear a node's status field — enforces valid lifecycle values client-side |
| `divoid_set_content` | Post content to a node's body — UTF-8 safe, no bash heredoc mangling; `path` must resolve inside the server's configured workspace root(s) |
| `divoid_edit_content` | Apply one or more partial edits to a node's text content in a single atomic request, addressed by 1-based line/char ranges |
| `divoid_delete_node` | Permanently delete a node by id; destructive and irreversible |
| `divoid_create_task` | Atomic create: makes the node, sets its content, links it to the project's Tasks group; accepts optional `access` param |
| `divoid_create_documentation` | Atomic create: makes the node, sets its content, links it to the project's Docs group; accepts optional `access` param |
| `divoid_create_session_log` | Atomic create: makes the node, sets its content, links it to the project's Docs group + any extra links; accepts optional `access` param |
| `divoid_create_node` | Generic atomic create for any node type — meeting, plan, project, group (type=None), event, or any custom type; optional content + extra_links; no content-required check or group auto-resolution |
| `divoid_resolve_user` | Look up a DiVoid user by name — returns the user id needed for message routing |
| `divoid_send_message` | Send a message to a DiVoid user's inbox |
| `divoid_list_messages` | List messages in a user's inbox, optionally filtered by project |
| `divoid_delete_message` | Delete a message from a user's inbox by message id |

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

**Filesystem path containment** (`divoid_download_content` and `divoid_set_content(path=...)`) is controlled via `DIVOID_MCP_FILE_ROOT`, an `os.pathsep`-separated list of directories (`;` on Windows, `:` on POSIX). If unset, the default root is the server process's working directory. A caller-supplied `path` that does not resolve inside one of these roots is rejected with `path_outside_root` before any disk or network I/O; if no configured root is usable, both tools return `file_root_unusable` for every call. See DiVoid **#10473** / **#10472** and `docs/architecture/mcp-path-containment.md` (DiVoid **#10479**).

**Never set `DIVOID_MCP_FILE_ROOT` to `~`, `~/.claude`, or a drive root (`C:\`, `/`).** Each of those defeats the containment this variable exists to provide — a home-directory root puts `~/.claude/secrets/.divoid-online` (the exact exfiltration target the finding named) back inside the fence, and a drive root makes "contained" mean "the whole volume." If a legitimate path is rejected, add its specific directory to the list — do not widen to one of these.

**In-root sensitive-path denial**: even a path that resolves inside a configured root is refused with `path_denied_sensitive` if any component of its resolved form names a credential-bearing or host-configuration file — `.git/**`, `.env*`, `.npmrc`, `.pypirc`, `.netrc`, `pip.conf`/`pip.ini`, `*.pem`/`*.key`/`*.pfx`/`*.p12`, `id_rsa`/`id_dsa`/`id_ecdsa`/`id_ed25519`, `.mcp.json`, `settings.json`, `settings.local.json`. This applies to both `divoid_download_content` and `divoid_set_content(path=...)` and there is no configuration override — an override would be the exfiltration/overwrite primitive this refusal exists to close. If a refusal ever names a file that is genuinely a legitimate document body, the fix is to **remove that pattern in a PR and re-measure**, never to add a bypass. A pattern must also never name a directory that can appear as an *ancestor* of a configured root (e.g. a hypothetical `.claude` pattern would break every session rooted under `.claude/worktrees/<purpose>`) -- the verdict must depend only on components *below* the matched root. See DiVoid **#10481** and `docs/architecture/mcp-in-root-exfiltration.md` (DiVoid **#10543**).

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

