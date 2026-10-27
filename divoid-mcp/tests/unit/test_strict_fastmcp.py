"""Unknown tool parameters fail at the call site through the registered tool path."""

from __future__ import annotations

import httpx
import pytest
import respx
from mcp.server.fastmcp.exceptions import ToolError

from divoid_mcp import http_client
from divoid_mcp.config import DivoidConfig
from divoid_mcp.strict_fastmcp import StrictFastMCP
from divoid_mcp.tools import register_tools

_BASE = "http://divoid.test"


@pytest.fixture
def server() -> StrictFastMCP:
    http_client.init(_BASE, "dummy-key")
    mcp_server = StrictFastMCP("divoid-mcp-test")
    mcp_server.config = DivoidConfig(  # type: ignore[attr-defined]
        base_url=_BASE, api_key="dummy-key", source="env"
    )
    register_tools(mcp_server)
    return mcp_server


@pytest.mark.parametrize(
    ("tool", "arguments", "unknown", "accepted"),
    [
        ("divoid_get_node", {"id": 1, "bogus_key": 2}, "bogus_key", "id"),
        (
            "divoid_create_documentation",
            {"name": "n", "content": "c", "link_to": [1]},
            "link_to",
            "extra_links",
        ),
        (
            "divoid_create_task",
            {"name": "n", "content": "c", "link_to": [1]},
            "link_to",
            "extra_links",
        ),
        ("divoid_search", {"query": "q", "nonsense": 1}, "nonsense", "query"),
    ],
)
async def test_call_tool_unknown_parameter_raises_naming_key_and_accepted(
    server: StrictFastMCP, tool: str, arguments: dict, unknown: str, accepted: str
) -> None:
    with respx.mock(assert_all_called=False) as mock:
        route = mock.route().mock(return_value=httpx.Response(200, json={}))
        with pytest.raises(ToolError) as caught:
            await server.call_tool(tool, arguments)

    message = str(caught.value)
    assert unknown in message.split("Accepted parameters:")[0]
    assert accepted in message.split("Accepted parameters:")[1]
    assert not route.called


async def test_call_tool_valid_arguments_still_reach_the_tool(server: StrictFastMCP) -> None:
    body = {"id": 7, "type": "task", "name": "n"}
    with respx.mock(assert_all_called=False) as mock:
        route = mock.get(f"{_BASE}/nodes/7").mock(return_value=httpx.Response(200, json=body))
        _, structured = await server.call_tool("divoid_get_node", {"id": 7})

    assert route.called
    assert structured["id"] == 7


async def test_call_tool_unknown_tool_name_keeps_the_library_error(server: StrictFastMCP) -> None:
    with pytest.raises(ToolError, match="Unknown tool"):
        await server.call_tool("divoid_no_such_tool", {"x": 1})
