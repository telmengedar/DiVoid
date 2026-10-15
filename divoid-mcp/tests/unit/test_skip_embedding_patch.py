"""Unit tests for `skip_embedding` on divoid_patch_node and its echo on divoid_get_node
(DiVoid #22474 / design #22475)."""

from __future__ import annotations

import json
from typing import Any

import httpx
import pytest
import respx
from mcp.server.fastmcp import FastMCP

from divoid_mcp import http_client
from divoid_mcp.config import DivoidConfig
from divoid_mcp.tools.get_node import register as register_get_node
from divoid_mcp.tools.patch_node import register as register_patch_node

_DUMMY_BASE = "http://divoid.test"
_DUMMY_KEY = "dummy-key-for-unit-tests"
_NODE_ID = 22601
_NODE_URL = f"{_DUMMY_BASE}/nodes/{_NODE_ID}"


@pytest.fixture(scope="module")
def server() -> FastMCP:
    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY, source="env")
    http_client.init(_DUMMY_BASE, _DUMMY_KEY)
    mcp_server = FastMCP("divoid-mcp-skip-embedding-patch-test")
    mcp_server.config = config  # type: ignore[attr-defined]
    register_patch_node(mcp_server)
    register_get_node(mcp_server)
    return mcp_server


async def _patch_and_capture_ops(server: FastMCP, args: dict[str, Any]) -> list[dict[str, Any]]:
    captured: list[httpx.Request] = []

    def patch(req: httpx.Request) -> httpx.Response:
        captured.append(req)
        return httpx.Response(200, json={"id": _NODE_ID})

    with respx.mock(assert_all_called=False) as mock:
        mock.patch(_NODE_URL).mock(side_effect=patch)
        result = await server._tool_manager.call_tool(
            "divoid_patch_node", {"id": _NODE_ID, **args}
        )

    assert isinstance(result, dict)
    assert result.get("isError") is not True, f"patch failed: {result}"
    assert len(captured) == 1
    return json.loads(captured[0].content)


@pytest.mark.asyncio
async def test_patch_node_skip_embedding_true_sends_replace(server: FastMCP) -> None:
    ops = await _patch_and_capture_ops(server, {"skip_embedding": True})

    assert ops == [{"op": "replace", "path": "/skipEmbedding", "value": True}]


@pytest.mark.asyncio
async def test_patch_node_skip_embedding_false_sends_replace(server: FastMCP) -> None:
    """False is a real value (opt back in), not unset."""
    ops = await _patch_and_capture_ops(server, {"skip_embedding": False})

    assert ops == [{"op": "replace", "path": "/skipEmbedding", "value": False}]


@pytest.mark.asyncio
async def test_patch_node_skip_embedding_none_sends_no_op(server: FastMCP) -> None:
    ops = await _patch_and_capture_ops(server, {"name": "renamed"})

    assert all(op["path"] != "/skipEmbedding" for op in ops), f"ops were {ops}"


@pytest.mark.asyncio
async def test_patch_node_skip_embedding_alone_satisfies_no_fields_guard(
    server: FastMCP,
) -> None:
    with respx.mock(assert_all_called=False) as mock:
        mock.patch(_NODE_URL).mock(return_value=httpx.Response(200, json={"id": _NODE_ID}))
        result = await server._tool_manager.call_tool(
            "divoid_patch_node", {"id": _NODE_ID, "skip_embedding": False}
        )

    assert result.get("isError") is not True, f"got: {result}"


@pytest.mark.asyncio
async def test_patch_node_skip_embedding_composes_with_other_fields(server: FastMCP) -> None:
    ops = await _patch_and_capture_ops(server, {"name": "n", "skip_embedding": True})

    assert {op["path"] for op in ops} == {"/name", "/skipEmbedding"}


@pytest.mark.parametrize("flag", [True, False])
@pytest.mark.asyncio
async def test_get_node_returns_skip_embedding(server: FastMCP, flag: bool) -> None:
    with respx.mock(assert_all_called=False) as mock:
        mock.get(_NODE_URL).mock(
            return_value=httpx.Response(200, json={"id": _NODE_ID, "skipEmbedding": flag})
        )
        result = await server._tool_manager.call_tool("divoid_get_node", {"id": _NODE_ID})

    assert result["skipEmbedding"] is flag


@pytest.mark.asyncio
async def test_get_node_skip_embedding_absent_from_older_backend_is_none(
    server: FastMCP,
) -> None:
    with respx.mock(assert_all_called=False) as mock:
        mock.get(_NODE_URL).mock(return_value=httpx.Response(200, json={"id": _NODE_ID}))
        result = await server._tool_manager.call_tool("divoid_get_node", {"id": _NODE_ID})

    assert "skipEmbedding" in result and result["skipEmbedding"] is None
