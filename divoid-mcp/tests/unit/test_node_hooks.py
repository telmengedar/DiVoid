from __future__ import annotations

import json

import httpx
import pytest
import respx
from mcp.server.fastmcp import FastMCP

from divoid_mcp import http_client
from divoid_mcp.config import DivoidConfig
from divoid_mcp.tools import register_tools


@pytest.fixture
def server():
    http_client.init("http://divoid.test", "dummy")
    server = FastMCP("hooks")
    server.config = DivoidConfig(base_url="http://divoid.test", api_key="dummy", source="env")
    register_tools(server)
    return server


@pytest.mark.parametrize("tool,method", [
    ("divoid_register_node_hook", "POST"),
    ("divoid_delete_node_hook", "DELETE"),
])
@respx.mock
async def test_hook_tuple_uses_json_body(server, tool, method):
    payload = {"nodeId": 7, "userId": 3, "tag": "Route"}
    route = respx.request(method, "http://divoid.test/nodehooks").mock(
        return_value=httpx.Response(200, json=payload) if method == "POST" else httpx.Response(200)
    )
    result = await server._tool_manager.call_tool(tool, {"node_id": 7, "user_id": 3, "tag": " Route "})
    assert json.loads(route.calls[0].request.content) == payload
    assert not result.get("isError")


@respx.mock
async def test_list_filters_and_default_scope(server):
    route = respx.get("http://divoid.test/nodehooks").mock(return_value=httpx.Response(200, json=[]))
    assert await server._tool_manager.call_tool("divoid_list_node_hooks", {}) == {"result": []}
    assert not route.calls[0].request.url.query
    await server._tool_manager.call_tool("divoid_list_node_hooks", {"node_id": 7, "user_id": 3})
    assert dict(route.calls[1].request.url.params) == {"nodeId": "7", "userId": "3"}


@pytest.mark.parametrize("args", [
    {"node_id": 0, "user_id": 3, "tag": "route"},
    {"node_id": 7, "user_id": -3, "tag": "route"},
    {"node_id": 7, "user_id": 3, "tag": " "},
    {"node_id": 7, "user_id": 3, "tag": "bad[tag"},
    {"node_id": 7, "user_id": 3, "tag": "a" * 129},
])
@respx.mock
async def test_invalid_hook_makes_no_http_call(server, args):
    result = await server._tool_manager.call_tool("divoid_register_node_hook", args)
    assert result["isError"] is True
    assert not respx.calls


@pytest.mark.parametrize("status", [401, 403, 404, 500])
@respx.mock
async def test_errors_use_existing_mapper(server, status):
    route = respx.post("http://divoid.test/nodehooks").mock(return_value=httpx.Response(status, json={}))
    result = await server._tool_manager.call_tool("divoid_register_node_hook", {"node_id": 7, "user_id": 3, "tag": "route"})
    assert result["isError"] is True
    assert route.call_count == 1


@respx.mock
async def test_network_failure_is_not_retried(server):
    route = respx.get("http://divoid.test/nodehooks").mock(side_effect=httpx.ConnectError("offline"))
    result = await server._tool_manager.call_tool("divoid_list_node_hooks", {})
    assert result["isError"] is True
    assert route.call_count == 1
