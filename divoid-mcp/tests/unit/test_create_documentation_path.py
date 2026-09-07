"""
Unit tests for divoid_create_documentation's `path` parameter: byte-identical
file upload, the no-orphan ordering guarantee, and containment refusals.
"""

from __future__ import annotations

from typing import Any

import httpx
import pytest
import respx
from mcp.server.fastmcp import FastMCP

from divoid_mcp import http_client, paths
from divoid_mcp.config import DivoidConfig
from divoid_mcp.tools.create_documentation import register as register_create_documentation

_DUMMY_BASE = "http://divoid.test"
_DUMMY_KEY = "dummy-key-for-unit-tests"

_DOCS_GROUP_ID = 7
_NEW_NODE_ID = 5001

_NODES_URL = f"{_DUMMY_BASE}/nodes"
_CONTENT_URL = f"{_DUMMY_BASE}/nodes/{_NEW_NODE_ID}/content"
_LINKS_URL = f"{_DUMMY_BASE}/nodes/{_NEW_NODE_ID}/links"

_TRAP_BYTES = (
    "| col `a` | value |\r\n"
    "| --- | --- |\r\n"
    "| pipe | a | b |\r\n"
    "unicode: über \U0001f30d box: └─ dash: —\r\n"
).encode("utf-8") + b"\rlone-cr-no-lf"


@pytest.fixture(scope="module")
def server() -> FastMCP:
    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY, source="env")
    http_client.init(_DUMMY_BASE, _DUMMY_KEY)

    mcp_server = FastMCP("divoid-mcp-create-documentation-path-test")
    mcp_server.config = config  # type: ignore[attr-defined]
    register_create_documentation(mcp_server)

    return mcp_server


@pytest.fixture(autouse=True)
def _configure_root(tmp_path: Any) -> None:
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(tmp_path)})


async def _call(server: FastMCP, args: dict[str, Any]) -> dict[str, Any]:
    result = await server._tool_manager.call_tool("divoid_create_documentation", args)
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


def _mock_links(mock: respx.MockRouter) -> None:
    mock.post(_LINKS_URL).mock(return_value=httpx.Response(200, json={}))


@pytest.mark.asyncio
async def test_path_uploads_trap_bytes_byte_identical(server: FastMCP, tmp_path: Any) -> None:
    """A file with an unescaped '|', CRLF, multi-byte chars, and a lone CR
    uploads byte-for-byte identical -- assert on the wire, not on a locally
    built value."""
    target = tmp_path / "trap.md"
    target.write_bytes(_TRAP_BYTES)

    node_captured: list[httpx.Request] = []
    content_captured: list[bytes] = []

    with respx.mock(assert_all_called=True) as mock:
        _mock_create_node(mock, node_captured)

        def capture_content(req: httpx.Request) -> httpx.Response:
            content_captured.append(req.content)
            return httpx.Response(200, json={})

        mock.post(_CONTENT_URL).mock(side_effect=capture_content)
        _mock_links(mock)

        result = await _call(server, {
            "name": "Doc from file",
            "path": str(target),
            "docs_group_id": _DOCS_GROUP_ID,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert content_captured[0] == _TRAP_BYTES, (
        f"Uploaded bytes differ from the source file -- a decode/re-encode step occurred.\n"
        f"  expected: {_TRAP_BYTES!r}\n"
        f"  actual:   {content_captured[0]!r}"
    )
    assert result.get("content_length") == len(_TRAP_BYTES)


@pytest.mark.asyncio
async def test_out_of_root_path_creates_no_node(server: FastMCP, tmp_path: Any) -> None:
    """Body resolution runs BEFORE the node POST, so a bad path issues zero
    HTTP calls -- no orphan node."""
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
            "name": "Doc from bad path",
            "path": str(secret),
            "docs_group_id": _DOCS_GROUP_ID,
        })

    assert result.get("isError") is True, f"Expected isError=True, got: {result}"
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
            "name": "Doc from sensitive path",
            "path": str(target),
            "docs_group_id": _DOCS_GROUP_ID,
        })

    assert result.get("isError") is True
    assert not node_posted, "POST /nodes must NOT be issued for a sensitive-path refusal."
    assert _error_code(result) == "path_denied_sensitive"


@pytest.mark.asyncio
async def test_empty_file_creates_no_node(server: FastMCP, tmp_path: Any) -> None:
    target = tmp_path / "empty.md"
    target.write_bytes(b"")
    node_posted = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal node_posted
            node_posted = True
            return httpx.Response(200, json={"id": _NEW_NODE_ID})

        mock.post(_NODES_URL).mock(side_effect=detect)

        result = await _call(server, {
            "name": "Doc from empty file",
            "path": str(target),
            "docs_group_id": _DOCS_GROUP_ID,
        })

    assert result.get("isError") is True
    assert not node_posted
    assert _error_code(result) == "file_empty"


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
            "name": "Doc with both content and path",
            "content": "inline body",
            "path": str(target),
            "docs_group_id": _DOCS_GROUP_ID,
        })

    assert result.get("isError") is True
    assert not node_posted
    assert _error_code(result) == "content_path_conflict"


@pytest.mark.asyncio
async def test_neither_content_nor_path_rejected_before_http(server: FastMCP) -> None:
    node_posted = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal node_posted
            node_posted = True
            return httpx.Response(200, json={"id": _NEW_NODE_ID})

        mock.post(_NODES_URL).mock(side_effect=detect)

        result = await _call(server, {
            "name": "Doc with neither content nor path",
            "docs_group_id": _DOCS_GROUP_ID,
        })

    assert result.get("isError") is True
    assert not node_posted
    assert _error_code(result) == "content_whitespace_only"


@pytest.mark.asyncio
async def test_whitespace_only_content_still_rejected_when_path_absent(server: FastMCP) -> None:
    """The per-tool required-check still applies to inline `content` -- adding
    `path` did not loosen the existing content_whitespace_only rule."""
    node_posted = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal node_posted
            node_posted = True
            return httpx.Response(200, json={"id": _NEW_NODE_ID})

        mock.post(_NODES_URL).mock(side_effect=detect)

        result = await _call(server, {
            "name": "Doc with whitespace-only content",
            "content": "   \n  ",
            "docs_group_id": _DOCS_GROUP_ID,
        })

    assert result.get("isError") is True
    assert not node_posted
    assert _error_code(result) == "content_whitespace_only"


@pytest.mark.asyncio
async def test_whitespace_only_file_uploaded_as_is(server: FastMCP, tmp_path: Any) -> None:
    """Unlike whitespace-only `content`, a whitespace-only FILE is uploaded
    verbatim, not refused."""
    target = tmp_path / "whitespace.md"
    target.write_bytes(b"   \n  ")

    content_captured: list[bytes] = []

    with respx.mock(assert_all_called=True) as mock:
        _mock_create_node(mock, [])

        def capture_content(req: httpx.Request) -> httpx.Response:
            content_captured.append(req.content)
            return httpx.Response(200, json={})

        mock.post(_CONTENT_URL).mock(side_effect=capture_content)
        _mock_links(mock)

        result = await _call(server, {
            "name": "Doc from whitespace-only file",
            "path": str(target),
            "docs_group_id": _DOCS_GROUP_ID,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert content_captured[0] == b"   \n  "
