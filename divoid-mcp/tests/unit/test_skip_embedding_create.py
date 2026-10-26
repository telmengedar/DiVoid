"""Unit tests for `skip_embedding` on the four create tools (DiVoid #22474 / design #22475)."""

from __future__ import annotations

import json
from collections.abc import Callable
from typing import Any

import httpx
import pytest
import respx
from mcp.server.fastmcp import FastMCP

from divoid_mcp import http_client
from divoid_mcp.config import DivoidConfig
from divoid_mcp.tools.create_documentation import register as register_documentation
from divoid_mcp.tools.create_node import register as register_node
from divoid_mcp.tools.create_session_log import _execute as _execute_session_log
from divoid_mcp.tools.create_session_log import register as register_session_log
from divoid_mcp.tools.create_task import register as register_task

_DUMMY_BASE = "http://divoid.test"
_DUMMY_KEY = "dummy-key-for-unit-tests"
_NEW_NODE_ID = 22600
_GROUP_ID = 7

_NODES_URL = f"{_DUMMY_BASE}/nodes"
_CONTENT_URL = f"{_DUMMY_BASE}/nodes/{_NEW_NODE_ID}/content"
_LINKS_URL = f"{_DUMMY_BASE}/nodes/{_NEW_NODE_ID}/links"

_TOOLS: list[tuple[str, Callable[[FastMCP], None], dict[str, Any]]] = [
    ("divoid_create_node", register_node, {"name": "n", "content": "body"}),
    (
        "divoid_create_documentation",
        register_documentation,
        {"name": "n", "content": "body", "docs_group_id": _GROUP_ID},
    ),
    (
        "divoid_create_session_log",
        register_session_log,
        {"name": "n", "content": "body", "docs_group_id": _GROUP_ID},
    ),
    (
        "divoid_create_task",
        register_task,
        {"name": "n", "content": "body", "tasks_group_id": _GROUP_ID},
    ),
]


def _server_for(register: Callable[[FastMCP], None]) -> FastMCP:
    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY, source="env")
    http_client.init(_DUMMY_BASE, _DUMMY_KEY)
    mcp_server = FastMCP("divoid-mcp-skip-embedding-create-test")
    mcp_server.config = config  # type: ignore[attr-defined]
    register(mcp_server)
    return mcp_server


async def _create_and_capture_node_body(
    tool_name: str,
    register: Callable[[FastMCP], None],
    args: dict[str, Any],
) -> dict[str, Any]:
    server = _server_for(register)
    captured: list[httpx.Request] = []

    def create(req: httpx.Request) -> httpx.Response:
        captured.append(req)
        return httpx.Response(200, json={"id": _NEW_NODE_ID, **json.loads(req.content)})

    with respx.mock(assert_all_called=False) as mock:
        mock.post(_NODES_URL).mock(side_effect=create)
        mock.post(_CONTENT_URL).mock(return_value=httpx.Response(200, json={}))
        mock.post(_LINKS_URL).mock(return_value=httpx.Response(200, json={}))
        result = await server._tool_manager.call_tool(tool_name, args)

    assert isinstance(result, dict)
    assert result.get("isError") is not True, f"create failed: {result}"
    assert len(captured) == 1
    return json.loads(captured[0].content)


@pytest.mark.parametrize("tool_name,register,args", _TOOLS, ids=[t[0] for t in _TOOLS])
@pytest.mark.asyncio
async def test_skip_embedding_true_sends_skipEmbedding_true(
    tool_name: str, register: Callable[[FastMCP], None], args: dict[str, Any]
) -> None:
    """skip_embedding=True puts skipEmbedding: true in the POST /nodes body."""
    body = await _create_and_capture_node_body(
        tool_name, register, {**args, "skip_embedding": True}
    )

    assert body.get("skipEmbedding") is True, f"body was {body}"


_SESSION_LOG = "divoid_create_session_log"
_FOCUSED_TOOLS = [t for t in _TOOLS if t[0] != _SESSION_LOG]
_SESSION_LOG_TOOL = next(t for t in _TOOLS if t[0] == _SESSION_LOG)


@pytest.mark.parametrize(
    "tool_name,register,args", _FOCUSED_TOOLS, ids=[t[0] for t in _FOCUSED_TOOLS]
)
@pytest.mark.asyncio
async def test_skip_embedding_omitted_leaves_key_out(
    tool_name: str, register: Callable[[FastMCP], None], args: dict[str, Any]
) -> None:
    """Default call sends a body without the key, so an older backend sees no change."""
    body = await _create_and_capture_node_body(tool_name, register, args)

    assert "skipEmbedding" not in body, f"body was {body}"


@pytest.mark.asyncio
async def test_session_log_skip_embedding_omitted_sends_true() -> None:
    """Session logs are unfocused dumps: omitting the flag skips the embedding."""
    _, register, args = _SESSION_LOG_TOOL
    body = await _create_and_capture_node_body(_SESSION_LOG, register, args)

    assert body.get("skipEmbedding") is True, f"body was {body}"


@pytest.mark.parametrize("tool_name,register,args", _TOOLS, ids=[t[0] for t in _TOOLS])
@pytest.mark.asyncio
async def test_skip_embedding_false_leaves_key_out(
    tool_name: str, register: Callable[[FastMCP], None], args: dict[str, Any]
) -> None:
    """An explicit False is the default and must not reach the wire either."""
    body = await _create_and_capture_node_body(
        tool_name, register, {**args, "skip_embedding": False}
    )

    assert "skipEmbedding" not in body, f"body was {body}"


@pytest.mark.parametrize("tool_name,register,args", _TOOLS, ids=[t[0] for t in _TOOLS])
@pytest.mark.asyncio
async def test_skip_embedding_does_not_disturb_other_body_fields(
    tool_name: str, register: Callable[[FastMCP], None], args: dict[str, Any]
) -> None:
    """Setting the flag changes the body by exactly one key."""
    plain = await _create_and_capture_node_body(
        tool_name, register, {**args, "skip_embedding": False}
    )
    flagged = await _create_and_capture_node_body(
        tool_name, register, {**args, "skip_embedding": True}
    )

    assert {k: v for k, v in flagged.items() if k != "skipEmbedding"} == plain


@pytest.mark.asyncio
async def test_session_log_core_default_sends_true() -> None:
    """Direct callers of the core function get the same default as the tool."""
    _server_for(register_session_log)
    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY, source="env")
    captured: list[httpx.Request] = []

    def create(req: httpx.Request) -> httpx.Response:
        captured.append(req)
        return httpx.Response(200, json={"id": _NEW_NODE_ID, **json.loads(req.content)})

    with respx.mock(assert_all_called=False) as mock:
        mock.post(_NODES_URL).mock(side_effect=create)
        mock.post(_CONTENT_URL).mock(return_value=httpx.Response(200, json={}))
        mock.post(_LINKS_URL).mock(return_value=httpx.Response(200, json={}))
        await _execute_session_log("n", "body", config, docs_group_id=_GROUP_ID)

    assert len(captured) == 1
    assert json.loads(captured[0].content).get("skipEmbedding") is True
