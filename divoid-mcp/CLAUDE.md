# CLAUDE.md — divoid-mcp

Agents working in this repo: read this before touching any code.

## What this repo is

A Python MCP server that wraps the DiVoid REST API. It is **a pure client wrapper** — it does not change DiVoid's backend, schema, or API. All mutations go through DiVoid's existing endpoints.

Architecture document: `docs/architecture/phase-1.md` and DiVoid node **#695** (canonical, read both).

## Tool surface

18 tools currently registered (see `src/divoid_mcp/tools/__init__.py`). Read-side: `divoid_search`, `divoid_get_node`, `divoid_get_content`, `divoid_get_links`, `divoid_list_nodes`. Mutate: `divoid_link_nodes`, `divoid_unlink_nodes`, `divoid_patch_node`, `divoid_set_status`, `divoid_set_content`. Composite create: `divoid_create_task`, `divoid_create_documentation`, `divoid_create_session_log`, `divoid_create_node` (generic escape hatch for any type). Messaging: `divoid_send_message`, `divoid_list_messages`, `divoid_delete_message`, `divoid_resolve_user`.

New tools require human sign-off from the repo owner before implementation — this is a generic-purpose tool used outside this deployment, so the surface evolves deliberately.

## Key invariants

1. **The API key never leaves the process boundary.** It lives in `config.py`'s frozen container and the HTTP client's pre-built header — nowhere else. Do not add it to log lines, error messages, tool return values, or any output.

2. **All logs go to stderr.** stdout is reserved for the JSON-RPC stream. A `print()` to stdout corrupts the MCP session.

3. **No retries.** The MCP layer is no-retries by design — see architecture §9.3. Adding retries for non-idempotent calls (POST node, POST link) without idempotency keys creates duplicates.

4. **Content is posted as `bytes`, not strings.** The UTF-8 mangling trap is in DiVoid node #187. `httpx` encodes the string to `bytes` before sending — this is done in `http_client.py`, not in tool dispatchers.

5. **Invariant guard runs before any HTTP call** in composite tools. Violations raise `InvariantViolation`; the dispatcher wraps it in an MCP error. Do not call HTTP before the guard.

6. **The system layer never enforces client *vocabulary*.** DiVoid provides the system; the *convention* — which status values a type carries, what they mean, which types even use status — is the client's scope and evolves as the client refines its process. So the MCP must **never** hard-code an allow-list of status values, node types, or any other free-form vocabulary and reject what the backend would accept. If the backend takes it, the tool passes it through. This is why `divoid_set_status` is a thin free-form PATCH wrapper with no lifecycle check (PR #157 / DiVoid #5837 removed the old `{new,open,in-progress,closed}` allow-list — it forced a REST fallback every time a new status was coined, and citing a client-convention doc like `#493 §5` as justification was the exact mistake). Invariant 5's guard is for *structural* invariants the backend genuinely requires (content-required types, lifecycle ORDER of operations) — not for policing vocabulary the backend leaves open. When unsure whether a rule is "structural invariant" or "client convention": if the backend accepts the value, it's convention — do not enforce it here.

## Repo layout

```
src/divoid_mcp/        # installable package
  server.py            # bootstrap — wires everything, calls mcp.serve_stdio()
  config.py            # reads ~/.claude/secrets/.divoid-online; fail-closed
  http_client.py       # shared async httpx client with auth header pre-set
  errors.py            # InvariantViolation + error_mapper
  drift.py             # startup canary against node #8 hash
  resources.py         # MCP resources for canonical DiVoid docs
  version.py           # __version__ + PINNED_API_REF_HASH
  tools/               # one module per registered tool; __init__.py wires them
tests/smoke/           # live integration scripts (not pytest); run with pip install -e .
docs/architecture/     # architecture docs committed here
examples/              # .mcp.json registration examples
```

## Running

```bash
pip install -e .
python -m divoid_mcp          # run server (will block on stdio)
python tests/smoke/run_all.py # run smoke tests against live DiVoid
```

## Code style

- Python 3.11+, type hints throughout
- 4-space indent, 100-char line limit (see `.editorconfig`)
- `if __name__ == "__main__"` guards in all runnable scripts
- Imports: stdlib first, third-party second, local last; one blank line between groups

## DiVoid references

- API reference: node **#8**
- Structural conventions: node **#493** (Tasks/Docs groups, content-required types)
- Hivemind Protocol: node **#190**
- Onboarding: node **#9**
- Messaging Protocol: node **#435**
