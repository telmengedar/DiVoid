# Architecture: divoid-mcp Phase 1

This document is committed here for discoverability by downstream agents and
reviewers. The **canonical version** lives on DiVoid at node **#695** — if
this file and that node conflict, node #695 wins.

Fetch the live version:

```bash
DIVOID_KEY=$(awk -F= '/^ApiKey=/{print $2}' ~/.claude/secrets/.divoid-online)
DIVOID_URL=$(awk -F= '/^Url=/{print $2}' ~/.claude/secrets/.divoid-online)
curl -H "Authorization: Bearer $DIVOID_KEY" "$DIVOID_URL/nodes/695/content"
```

## Summary

divoid-mcp is a stdio MCP server that wraps the DiVoid REST API. It exposes:

- **Phase 1 (this PR):** four read-side tools + link_nodes
  - `divoid_search` — semantic search
  - `divoid_get_node` — node properties
  - `divoid_get_content` — content body
  - `divoid_link_nodes` — create a graph link
  - Five MCP resources at `divoid://node/{9,190,8,493,435}`

- **Phase 2 (separate PRs):** composite create tools, list/patch/set/message tools

## Key design decisions

See node #695 for the full document. Highlights:

- **Stdio transport only** — no HTTP listener, no port binding.
- **Fail-closed auth** — missing/malformed secret file exits the process before the stdio loop.
- **No retries** — non-idempotent calls (POST) must not be retried blindly.
- **No caching** — every call is live; the startup drift canary is the only point-in-time snapshot.
- **UTF-8 safety** — content is posted as `bytes` via httpx; no shell heredoc path.
- **API key in two places only** — `config.py` frozen container + HTTP client Authorization header.
- **Logs to stderr only** — stdout carries the JSON-RPC stream.
- **Drift canary** — startup-only, warns on mismatch, never blocks.

## Open questions resolved before implementation

From architecture doc §17:

1. MCP SDK pin: **exact** (`mcp == 1.27.1`)
2. `divoid_search` does NOT expose `nostatus`/`nototal` in Phase 1
3. Missing Tasks/Docs group: **fail loudly** with hint (Phase 2 composites)
4. `extra_links` targets: do NOT preflight-check existence (Phase 2 composites)
5. Drift canary: **startup-only**, log WARN on mismatch, do not block
6. MCP `resources` capability: **ship**, degrade gracefully if host ignores
7. Logging: **stderr only** for Phase 1

## References

- Proposal: DiVoid #692
- Phase 1 task: DiVoid #694
- Architecture doc: DiVoid #695
- API reference: DiVoid #8
- Structural conventions: DiVoid #493
- Hivemind Protocol: DiVoid #190
