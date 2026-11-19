"""Select the default MCP server or the package-owned read-only inbox event bridge."""

from __future__ import annotations

import sys


def main() -> int | None:
    arguments = sys.argv[1:]
    if arguments == ["--inbox-events"]:
        from .inbox_events import main as events_main
        return events_main()
    if arguments in (["--help"], ["-h"]):
        print("usage: divoid-mcp [--inbox-events]")
        return 0
    if arguments:
        print("divoid_mcp error=arguments", file=sys.stderr, flush=True)
        return 2
    from .server import main as server_main
    return server_main()
