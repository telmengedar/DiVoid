# Smoke Tests — divoid-mcp Phase 1

These are live integration scripts. They call the real DiVoid instance and
assert structurally correct responses. No mocking, no pytest infrastructure.

## Prerequisites

- Python 3.11+
- `~/.claude/secrets/.divoid-online` must exist and contain valid credentials
- The package must be installed: `pip install -e .` from the repo root

## Running

```bash
cd C:\dev\claude\divoid-mcp
pip install -e .
python tests/smoke/run_all.py
```

Expected output (all passing):

```
divoid-mcp Phase 1 smoke tests
==================================================

--- divoid_search ---
  [PASS] HTTP 200
  [PASS] has 'result' key
  [PASS] has 'total' key
  [PASS] result is list
  [PASS] total is int

--- divoid_search (type=documentation filter) ---
  [PASS] HTTP 200 with type filter
  [PASS] only documentation nodes returned

--- divoid_get_node ---
  [PASS] HTTP 200 for node #9
  [PASS] id is 9
  [PASS] has type field
  [PASS] has name field
  [PASS] type is non-empty string

--- divoid_get_node (not found) ---
  [PASS] 404 for non-existent node

--- divoid_get_content ---
  [PASS] HTTP 200 for node #9 content
  [PASS] content is non-empty
  [PASS] content decodes as UTF-8
  [PASS] content starts with text-like bytes

--- divoid_get_content (drift hash check) ---
  [PASS] HTTP 200 for node #8 content
  [PASS] node #8 hash matches pinned constant

--- divoid_link_nodes ---
  [PASS] link precondition: nodes #8 and #9 exist
  [PASS] POST /nodes/8/links → 200 or 2xx
  [PASS] GET /nodes/links → 200
  [PASS] link #8 ↔ #9 appears in adjacency result

==================================================
Results: 17/17 passed
All checks passed.
```

## What each test asserts

| Test | Asserts |
|---|---|
| `smoke_search` | `GET /nodes?query=...` returns `{result: [...], total: int}` |
| `smoke_search_with_type_filter` | `type=documentation` filter returns only docs |
| `smoke_get_node` | `GET /nodes/9` returns `{id, type, name, ...}` with correct id |
| `smoke_get_node_not_found` | `GET /nodes/999999999` returns HTTP 404 |
| `smoke_get_content` | `GET /nodes/9/content` returns non-empty UTF-8 text |
| `smoke_get_content_api_reference` | Node #8 hash matches the pinned constant in `version.py` |
| `smoke_link_nodes` | `POST /nodes/8/links` with target 9 succeeds; link visible in adjacency |

## When the drift hash check fails

If `smoke_get_content_api_reference` fails with a hash mismatch, the node #8
content has changed since the pin was recorded. This is expected when the API
reference is updated. To update the pin:

1. Run: `python -c "import hashlib; from divoid_mcp import http_client, config; import asyncio; ..."`
   Or more simply:
   ```bash
   DIVOID_KEY=$(awk -F= '/^ApiKey=/{print $2}' ~/.claude/secrets/.divoid-online)
   DIVOID_URL=$(awk -F= '/^Url=/{print $2}' ~/.claude/secrets/.divoid-online)
   curl -s -H "Authorization: Bearer $DIVOID_KEY" "$DIVOID_URL/nodes/8/content" | sha256sum
   ```
2. Update `PINNED_API_REF_HASH` in `src/divoid_mcp/version.py`.
3. Commit with a message describing what changed in node #8.

See `docs/drift-policy.md` for the full procedure.
