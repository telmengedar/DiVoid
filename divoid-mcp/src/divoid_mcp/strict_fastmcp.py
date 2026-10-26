"""FastMCP subclass that rejects tool calls carrying parameters the tool does not declare."""

from __future__ import annotations

from collections.abc import Sequence
from typing import Any

from mcp.server.fastmcp import FastMCP
from mcp.server.fastmcp.exceptions import ToolError
from mcp.types import ContentBlock


class StrictFastMCP(FastMCP):
    """FastMCP whose call_tool fails on unknown parameters, naming them and the accepted ones."""

    async def call_tool(
        self, name: str, arguments: dict[str, Any]
    ) -> Sequence[ContentBlock] | dict[str, Any]:
        for tool in await self.list_tools():
            if tool.name == name:
                accepted = set(tool.inputSchema.get("properties", {}))
                unknown = sorted(set(arguments) - accepted)
                if unknown:
                    raise ToolError(
                        f"Unknown parameter(s) for {name}: {', '.join(unknown)}. "
                        f"Accepted parameters: {', '.join(sorted(accepted)) or '(none)'}."
                    )
                break
        return await super().call_tool(name, arguments)
