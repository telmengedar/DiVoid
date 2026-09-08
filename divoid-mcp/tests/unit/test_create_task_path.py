"""
Unit tests for divoid_create_task's `path` parameter: satisfies the
content-required rule via a file, the status='new' escape still permits
neither input, the no-orphan ordering guarantee, and containment refusals.
"""

from __future__ import annotations

from typing import Any

import httpx
import pytest
import respx
from mcp.server.fastmcp import FastMCP

from divoid_mcp import http_client, paths
from divoid_mcp.config import DivoidConfig
from divoid_mcp.tools.create_task import register as register_create_task

_DUMMY_BASE = "http://divoid.test"
_DUMMY_KEY = "dummy-key-for-unit-tests"

_TASKS_GROUP_ID = 314
_NEW_NODE_ID = 6001

_NODES_URL = f"{_DUMMY_BASE}/nodes"
_CONTENT_URL = f"{_DUMMY_BASE}/nodes/{_NEW_NODE_ID}/content"
_LINKS_URL = f"{_DUMMY_BASE}/nodes/{_NEW_NODE_ID}/links"


@pytest.fixture(scope="module")
def server() -> FastMCP:
    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY, source="env")
    http_client.init(_DUMMY_BASE, _DUMMY_KEY)

    mcp_server = FastMCP("divoid-mcp-create-task-path-test")
    mcp_server.config = config  # type: ignore[attr-defined]
    register_create_task(mcp_server)

    return mcp_server


@pytest.fixture(autouse=True)
def _configure_root(tmp_path: Any) -> None:
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(tmp_path)})


async def _call(server: FastMCP, args: dict[str, Any]) -> dict[str, Any]:
    result = await server._tool_manager.call_tool("divoid_create_task", args)
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
async def test_path_satisfies_content_required_for_non_new_status(
    server: FastMCP, tmp_path: Any
) -> None:
    target = tmp_path / "scope.md"
    target.write_bytes(b"current state, what is missing, order of work")

    content_captured: list[bytes] = []

    with respx.mock(assert_all_called=True) as mock:
        _mock_create_node(mock, [])

        def capture_content(req: httpx.Request) -> httpx.Response:
            content_captured.append(req.content)
            return httpx.Response(200, json={})

        mock.post(_CONTENT_URL).mock(side_effect=capture_content)
        _mock_links(mock)

        result = await _call(server, {
            "name": "Task from file",
            "path": str(target),
            "status": "open",
            "tasks_group_id": _TASKS_GROUP_ID,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert content_captured[0] == b"current state, what is missing, order of work"
    assert result.get("content_length") == len(content_captured[0])


@pytest.mark.asyncio
async def test_status_new_neither_content_nor_path_succeeds_with_no_content_post(
    server: FastMCP,
) -> None:
    """status='new' is the quick-capture escape -- satisfiable by neither
    content nor path, and no content POST is issued at all."""
    node_captured: list[httpx.Request] = []
    content_posted = False

    with respx.mock(assert_all_called=False) as mock:
        _mock_create_node(mock, node_captured)

        def detect_content(req: httpx.Request) -> httpx.Response:
            nonlocal content_posted
            content_posted = True
            return httpx.Response(200, json={})

        mock.post(_CONTENT_URL).mock(side_effect=detect_content)
        _mock_links(mock)

        result = await _call(server, {
            "name": "Quick capture",
            "status": "new",
            "tasks_group_id": _TASKS_GROUP_ID,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(node_captured) == 1, "The node itself must still be created."
    assert not content_posted, "No content POST should be issued when status='new' and no body is given."
    assert result.get("content_length") == 0


@pytest.mark.asyncio
async def test_status_new_empty_string_content_issues_no_content_post(server: FastMCP) -> None:
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
        _mock_links(mock)

        result = await _call(server, {
            "name": "Quick capture with empty string content",
            "content": "",
            "status": "new",
            "tasks_group_id": _TASKS_GROUP_ID,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(node_captured) == 1
    assert not content_posted, "content='' must not issue a content POST."
    assert result.get("content_length") == 0


@pytest.mark.asyncio
async def test_status_new_whitespace_only_content_issues_no_content_post(server: FastMCP) -> None:
    node_captured: list[httpx.Request] = []
    content_posted = False

    with respx.mock(assert_all_called=False) as mock:
        _mock_create_node(mock, node_captured)

        def detect_content(req: httpx.Request) -> httpx.Response:
            nonlocal content_posted
            content_posted = True
            return httpx.Response(200, json={})

        mock.post(_CONTENT_URL).mock(side_effect=detect_content)
        _mock_links(mock)

        result = await _call(server, {
            "name": "Quick capture with whitespace-only content",
            "content": "  ",
            "status": "new",
            "tasks_group_id": _TASKS_GROUP_ID,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(node_captured) == 1
    assert not content_posted, "content='  ' must not issue a content POST."
    assert result.get("content_length") == 0


@pytest.mark.asyncio
async def test_content_required_when_status_not_new_and_neither_given(server: FastMCP) -> None:
    node_posted = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal node_posted
            node_posted = True
            return httpx.Response(200, json={"id": _NEW_NODE_ID})

        mock.post(_NODES_URL).mock(side_effect=detect)

        result = await _call(server, {
            "name": "Task with neither content nor path",
            "status": "open",
            "tasks_group_id": _TASKS_GROUP_ID,
        })

    assert result.get("isError") is True
    assert not node_posted
    assert _error_code(result) == "content_required"


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
            "name": "Task with both content and path",
            "content": "inline",
            "path": str(target),
            "status": "open",
            "tasks_group_id": _TASKS_GROUP_ID,
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
            "name": "Task from bad path",
            "path": str(secret),
            "status": "open",
            "tasks_group_id": _TASKS_GROUP_ID,
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
            "name": "Task from sensitive path",
            "path": str(target),
            "status": "open",
            "tasks_group_id": _TASKS_GROUP_ID,
        })

    assert result.get("isError") is True
    assert not node_posted
    assert _error_code(result) == "path_denied_sensitive"
