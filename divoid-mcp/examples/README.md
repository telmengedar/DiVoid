# MCP registration examples

Two files here, one per path shape — see `docs/install.md` Step 3 for the full walkthrough and the `claude mcp add` / Claude Desktop equivalents.

- **`.mcp.json`** — Linux/macOS (POSIX). Substitute your real home directory for `<you>`: `/home/<you>/...` on Linux, `/Users/<you>/...` on macOS.
- **`.mcp.windows.json`** — Windows. Substitute your real username for `<you>`: `C:\Users\<you>\...`.

Both point at the `divoid-mcp` console script inside the dedicated venv from `docs/install.md` Step 1 (`~/.divoid-mcp/venv`, or `%USERPROFILE%\.divoid-mcp\venv` on Windows) — never a bare command name or `python -m divoid_mcp`, and never a literal `~` in the JSON (JSON does not expand it).
