"""Unit tests for divoid_create_node's rootNodeId result reporting. DiVoid #11284."""

from __future__ import annotations

import json
from typing import Any

import httpx
import pytest
import respx
from mcp.server.fastmcp import FastMCP

from divoid_mcp import http_client
from divoid_mcp.config import DivoidConfig
from divoid_mcp.tools.create_node import register as register_create_node

_DUMMY_BASE = "http://divoid.test"
_DUMMY_KEY = "dummy-key-for-unit-tests"
_NEW_NODE_ID = 424242
_NODES_URL = f"{_DUMMY_BASE}/nodes"


@pytest.fixture(scope="module")
def server() -> FastMCP:
    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)
    http_client.init(_DUMMY_BASE, _DUMMY_KEY)

    mcp_server = FastMCP("divoid-mcp-create-node-root-test")
    mcp_server.config = config  # type: ignore[attr-defined]
    register_create_node(mcp_server)

    return mcp_server


async def _call(server: FastMCP, args: dict[str, Any]) -> dict[str, Any]:
    result = await server._tool_manager.call_tool("divoid_create_node", args)
    assert isinstance(result, dict), f"Expected dict, got {type(result)}"
    return result


@pytest.mark.asyncio
async def test_result_prefers_server_echoed_root_node_id_over_input(server: FastMCP) -> None:
    requested_root = 10
    server_assigned_root = 99
    assert server_assigned_root != requested_root

    def create(req: httpx.Request) -> httpx.Response:
        body = json.loads(req.content)
        assert body.get("rootNodeId") == requested_root
        return httpx.Response(200, json={"id": _NEW_NODE_ID, "rootNodeId": server_assigned_root})

    with respx.mock(assert_all_called=True) as mock:
        mock.post(_NODES_URL).mock(side_effect=create)

        result = await _call(server, {
            "name": "Generic node with explicit root_node_id",
            "type": "meeting",
            "root_node_id": requested_root,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert result["rootNodeId"] == server_assigned_root, (
        f"Expected the tool to report the server's own rootNodeId="
        f"{server_assigned_root}, not the locally-requested {requested_root}; "
        f"got: {result!r}"
    )


@pytest.mark.asyncio
async def test_result_falls_back_to_input_when_response_omits_root_node_id(server: FastMCP) -> None:
    requested_root = 15

    def create(req: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"id": _NEW_NODE_ID})

    with respx.mock(assert_all_called=True) as mock:
        mock.post(_NODES_URL).mock(side_effect=create)

        result = await _call(server, {
            "name": "Generic node, backend response omits rootNodeId",
            "type": "meeting",
            "root_node_id": requested_root,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert result["rootNodeId"] == requested_root, (
        f"Expected fallback to the requested rootNodeId={requested_root} when the "
        f"response omits the key, got: {result!r}"
    )


@pytest.mark.asyncio
async def test_result_reports_null_when_neither_supplied_nor_echoed(server: FastMCP) -> None:
    def create(req: httpx.Request) -> httpx.Response:
        body = json.loads(req.content)
        assert "rootNodeId" not in body
        return httpx.Response(200, json={"id": _NEW_NODE_ID})

    with respx.mock(assert_all_called=True) as mock:
        mock.post(_NODES_URL).mock(side_effect=create)

        result = await _call(server, {
            "name": "Generic node, no root_node_id at all",
            "type": "meeting",
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert result["rootNodeId"] is None, f"Expected honest null, got: {result!r}"
