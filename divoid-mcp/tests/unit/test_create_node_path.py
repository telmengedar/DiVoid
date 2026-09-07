"""
Unit tests for divoid_create_node's `path` parameter: content is fully
optional (unlike the type-specific creators), at most one of content/path,
the no-orphan ordering guarantee, and containment refusals.
"""

from __future__ import annotations

from typing import Any

import httpx
import pytest
import respx
from mcp.server.fastmcp import FastMCP

from divoid_mcp import http_client, paths
from divoid_mcp.config import DivoidConfig
from divoid_mcp.tools.create_node import register as register_create_node

_DUMMY_BASE = "http://divoid.test"
_DUMMY_KEY = "dummy-key-for-unit-tests"

_NEW_NODE_ID = 8001

_NODES_URL = f"{_DUMMY_BASE}/nodes"
_CONTENT_URL = f"{_DUMMY_BASE}/nodes/{_NEW_NODE_ID}/content"


@pytest.fixture(scope="module")
def server() -> FastMCP:
    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY, source="env")
    http_client.init(_DUMMY_BASE, _DUMMY_KEY)

    mcp_server = FastMCP("divoid-mcp-create-node-path-test")
    mcp_server.config = config  # type: ignore[attr-defined]
    register_create_node(mcp_server)

    return mcp_server


@pytest.fixture(autouse=True)
def _configure_root(tmp_path: Any) -> None:
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(tmp_path)})


async def _call(server: FastMCP, args: dict[str, Any]) -> dict[str, Any]:
    result = await server._tool_manager.call_tool("divoid_create_node", args)
    assert isinstance(result, dict), f"Expected dict, got {type(result)}"
    return result


def _error_code(result: dict[str, Any]) -> str:
    text = result.get("content", [{}])[0].get("text", "")
    return text.split(":", 1)[0]


def _mock_create_node(mock: respx.MockRouter, captured: list[httpx.Request]) -> None:
    def create(req: httpx.Request) -> httpx.Response:
        captured.append(req)
        return httpx.Response(200, json={"id": _NEW_NODE_ID})

    mock.post(_NODES_URL).mock(side_effect=create)


@pytest.mark.asyncio
async def test_path_uploads_file_body_byte_identical(server: FastMCP, tmp_path: Any) -> None:
    body = b"arbitrary generic-node body from a file"
    target = tmp_path / "body.md"
    target.write_bytes(body)

    content_captured: list[bytes] = []

    with respx.mock(assert_all_called=True) as mock:
        _mock_create_node(mock, [])

        def capture_content(req: httpx.Request) -> httpx.Response:
            content_captured.append(req.content)
            return httpx.Response(200, json={})

        mock.post(_CONTENT_URL).mock(side_effect=capture_content)

        result = await _call(server, {
            "name": "Generic node from file",
            "type": "meeting",
            "path": str(target),
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert content_captured[0] == body
    assert result.get("content_length") == len(body)


@pytest.mark.asyncio
async def test_neither_content_nor_path_creates_content_empty_node(server: FastMCP) -> None:
    """Unlike the type-specific creators, create_node has no content-required
    invariant -- neither content nor path is a legal, successful call."""
    node_captured: list[httpx.Request] = []
    content_posted = False

    with respx.mock(assert_all_called=False) as mock:
        _mock_create_node(mock, node_captured)

        def detect_content(req: httpx.Request) -> httpx.Response:
            nonlocal content_posted
            content_posted = True
            return httpx.Response(200, json={})

        mock.post(_CONTENT_URL).mock(side_effect=detect_content)

        result = await _call(server, {"name": "Untyped group node", "type": None})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(node_captured) == 1
    assert not content_posted, "No content POST should be issued when neither content nor path is given."
    assert result.get("content_length") == 0


@pytest.mark.asyncio
async def test_empty_string_content_issues_no_content_post(server: FastMCP) -> None:
    """content='' must not turn into a zero-byte content POST -- the exact
    outcome resolve_body's own file_empty guard refuses for path uploads."""
    node_captured: list[httpx.Request] = []
    content_posted = False

    with respx.mock(assert_all_called=False) as mock:
        _mock_create_node(mock, node_captured)

        def detect_content(req: httpx.Request) -> httpx.Response:
            nonlocal content_posted
            content_posted = True
            return httpx.Response(200, json={})

        mock.post(_CONTENT_URL).mock(side_effect=detect_content)

        result = await _call(server, {"name": "Node with empty string content", "content": ""})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(node_captured) == 1
    assert not content_posted, "content='' must not issue a content POST."
    assert result.get("content_length") == 0


@pytest.mark.asyncio
async def test_whitespace_only_content_issues_no_content_post(server: FastMCP) -> None:
    node_captured: list[httpx.Request] = []
    content_posted = False

    with respx.mock(assert_all_called=False) as mock:
        _mock_create_node(mock, node_captured)

        def detect_content(req: httpx.Request) -> httpx.Response:
            nonlocal content_posted
            content_posted = True
            return httpx.Response(200, json={})

        mock.post(_CONTENT_URL).mock(side_effect=detect_content)

        result = await _call(server, {"name": "Node with whitespace-only content", "content": "   "})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(node_captured) == 1
    assert not content_posted, "content='   ' must not issue a content POST."
    assert result.get("content_length") == 0


@pytest.mark.asyncio
async def test_both_content_and_path_rejected_before_http(server: FastMCP, tmp_path: Any) -> None:
    target = tmp_path / "both.md"
    target.write_bytes(b"file body")
    node_posted = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal node_posted
            node_posted = True
            return httpx.Response(200, json={"id": _NEW_NODE_ID})

        mock.post(_NODES_URL).mock(side_effect=detect)

        result = await _call(server, {
            "name": "Node with both content and path",
            "content": "inline",
            "path": str(target),
        })

    assert result.get("isError") is True
    assert not node_posted
    assert _error_code(result) == "content_path_conflict"


@pytest.mark.asyncio
async def test_out_of_root_path_creates_no_node(server: FastMCP, tmp_path: Any) -> None:
    root_dir = tmp_path / "workspace"
    evil_dir = tmp_path / "workspace-evil"
    root_dir.mkdir()
    evil_dir.mkdir()
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    secret = evil_dir / "secret.txt"
    secret.write_bytes(b"should never leave this directory")

    node_posted = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal node_posted
            node_posted = True
            return httpx.Response(200, json={"id": _NEW_NODE_ID})

        mock.post(_NODES_URL).mock(side_effect=detect)

        result = await _call(server, {
            "name": "Node from bad path",
            "path": str(secret),
        })

    assert result.get("isError") is True
    assert not node_posted, "POST /nodes must NOT be issued when the path is refused -- no orphan node."
    assert _error_code(result) == "path_outside_root"


@pytest.mark.asyncio
async def test_sensitive_in_root_path_creates_no_node(server: FastMCP, tmp_path: Any) -> None:
    root_dir = tmp_path / "workspace"
    git_dir = root_dir / ".git"
    git_dir.mkdir(parents=True)
    target = git_dir / "config"
    target.write_bytes(b"[remote]token=shhh")
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    node_posted = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal node_posted
            node_posted = True
            return httpx.Response(200, json={"id": _NEW_NODE_ID})

        mock.post(_NODES_URL).mock(side_effect=detect)

        result = await _call(server, {
            "name": "Node from sensitive path",
            "path": str(target),
        })

    assert result.get("isError") is True
    assert not node_posted
    assert _error_code(result) == "path_denied_sensitive"
