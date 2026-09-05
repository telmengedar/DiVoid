"""Unit tests for divoid_create_task's root_node_id defaulting and result reporting.
DiVoid #10986 / #11284."""

from __future__ import annotations

import json
from typing import Any

import httpx
import pytest
import respx
from mcp.server.fastmcp import FastMCP

from divoid_mcp import http_client
from divoid_mcp.config import DivoidConfig
from divoid_mcp.tools.create_task import register as register_create_task

_DUMMY_BASE = "http://divoid.test"
_DUMMY_KEY = "dummy-key-for-unit-tests"

_PROJECT_ID = 42
_TASKS_GROUP_ID = 314
_NEW_NODE_ID = 999

_NODES_URL = f"{_DUMMY_BASE}/nodes"
_CONTENT_URL = f"{_DUMMY_BASE}/nodes/{_NEW_NODE_ID}/content"
_LINKS_URL = f"{_DUMMY_BASE}/nodes/{_NEW_NODE_ID}/links"

_NO_OVERRIDE = object()


@pytest.fixture(scope="module")
def server() -> FastMCP:
    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)
    http_client.init(_DUMMY_BASE, _DUMMY_KEY)

    mcp_server = FastMCP("divoid-mcp-create-task-test")
    mcp_server.config = config  # type: ignore[attr-defined]
    register_create_task(mcp_server)

    return mcp_server


async def _call(server: FastMCP, args: dict[str, Any]) -> dict[str, Any]:
    result = await server._tool_manager.call_tool("divoid_create_task", args)
    assert isinstance(result, dict), f"Expected dict, got {type(result)}"
    return result


def _mock_group_resolution(mock: respx.MockRouter) -> None:
    mock.get(_NODES_URL).mock(
        return_value=httpx.Response(200, json={"result": [{"id": _TASKS_GROUP_ID}]})
    )


def _mock_create_node(
    mock: respx.MockRouter,
    captured: list[httpx.Request],
    *,
    echo_root: bool = True,
    override_root_node_id: Any = _NO_OVERRIDE,
) -> None:
    def create(req: httpx.Request) -> httpx.Response:
        captured.append(req)
        body = json.loads(req.content)
        response_body = {"id": _NEW_NODE_ID, **body}
        if override_root_node_id is not _NO_OVERRIDE:
            response_body["rootNodeId"] = override_root_node_id
        elif not echo_root:
            response_body.pop("rootNodeId", None)
        return httpx.Response(200, json=response_body)

    mock.post(_NODES_URL).mock(side_effect=create)


def _mock_content_and_links(mock: respx.MockRouter) -> None:
    mock.post(_CONTENT_URL).mock(return_value=httpx.Response(200, json={}))
    mock.post(_LINKS_URL).mock(return_value=httpx.Response(200, json={}))


def _create_body(captured: list[httpx.Request]) -> dict[str, Any]:
    assert len(captured) == 1, f"Expected exactly one POST /nodes call, got {len(captured)}"
    return json.loads(captured[0].content)


@pytest.mark.asyncio
async def test_project_id_only_defaults_root_node_id(server: FastMCP) -> None:
    captured: list[httpx.Request] = []

    with respx.mock(assert_all_called=True) as mock:
        _mock_group_resolution(mock)
        _mock_create_node(mock, captured)
        _mock_content_and_links(mock)

        result = await _call(server, {
            "name": "Task with only project_id",
            "content": "scope",
            "project_id": _PROJECT_ID,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    body = _create_body(captured)
    assert body.get("rootNodeId") == _PROJECT_ID, (
        f"Expected the POST /nodes body to carry rootNodeId={_PROJECT_ID}, got: {body!r}"
    )
    assert result["rootNodeId"] == _PROJECT_ID, (
        f"Expected the tool result to surface rootNodeId={_PROJECT_ID}, got: {result!r}"
    )
    assert result["tasks_group_id"] == _TASKS_GROUP_ID


@pytest.mark.asyncio
async def test_explicit_root_node_id_wins_over_project_id(server: FastMCP) -> None:
    captured: list[httpx.Request] = []
    explicit_root = 777
    assert explicit_root != _PROJECT_ID

    with respx.mock(assert_all_called=True) as mock:
        _mock_group_resolution(mock)
        _mock_create_node(mock, captured)
        _mock_content_and_links(mock)

        result = await _call(server, {
            "name": "Task with differing explicit root_node_id",
            "content": "scope",
            "project_id": _PROJECT_ID,
            "root_node_id": explicit_root,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    body = _create_body(captured)
    assert body.get("rootNodeId") == explicit_root, (
        f"Expected explicit root_node_id={explicit_root} to win over project_id={_PROJECT_ID}, "
        f"got POST body: {body!r}"
    )
    assert result["rootNodeId"] == explicit_root


@pytest.mark.asyncio
async def test_explicit_falsy_root_node_id_zero_wins_over_project_id(server: FastMCP) -> None:
    captured: list[httpx.Request] = []

    with respx.mock(assert_all_called=True) as mock:
        _mock_group_resolution(mock)
        _mock_create_node(mock, captured)
        _mock_content_and_links(mock)

        result = await _call(server, {
            "name": "Task with explicit root_node_id=0",
            "content": "scope",
            "project_id": _PROJECT_ID,
            "root_node_id": 0,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    body = _create_body(captured)
    assert body.get("rootNodeId") == 0, (
        f"Expected explicit root_node_id=0 to survive rather than be replaced by "
        f"project_id={_PROJECT_ID}, got POST body: {body!r}"
    )
    assert result["rootNodeId"] == 0, f"Expected result rootNodeId=0, got: {result!r}"


@pytest.mark.asyncio
async def test_tasks_group_id_without_root_node_id_stays_null(server: FastMCP) -> None:
    captured: list[httpx.Request] = []

    with respx.mock(assert_all_called=True) as mock:
        _mock_create_node(mock, captured)
        _mock_content_and_links(mock)

        result = await _call(server, {
            "name": "Task via tasks_group_id, no root_node_id",
            "content": "scope",
            "tasks_group_id": _TASKS_GROUP_ID,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    body = _create_body(captured)
    assert "rootNodeId" not in body, (
        f"Expected no rootNodeId key on the POST body when only tasks_group_id is given, "
        f"got: {body!r}"
    )
    assert result["rootNodeId"] is None, (
        f"Expected the tool result to report rootNodeId=None, got: {result!r}"
    )
    assert result["tasks_group_id"] == _TASKS_GROUP_ID


@pytest.mark.asyncio
async def test_tasks_group_id_with_explicit_root_node_id_applies_it(server: FastMCP) -> None:
    captured: list[httpx.Request] = []
    explicit_root = 555

    with respx.mock(assert_all_called=True) as mock:
        _mock_create_node(mock, captured)
        _mock_content_and_links(mock)

        result = await _call(server, {
            "name": "Task via tasks_group_id with explicit root_node_id",
            "content": "scope",
            "tasks_group_id": _TASKS_GROUP_ID,
            "root_node_id": explicit_root,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    body = _create_body(captured)
    assert body.get("rootNodeId") == explicit_root, (
        f"Expected explicit root_node_id={explicit_root} to apply, got POST body: {body!r}"
    )
    assert result["rootNodeId"] == explicit_root


@pytest.mark.asyncio
async def test_root_node_id_falls_back_when_response_omits_it(server: FastMCP) -> None:
    captured: list[httpx.Request] = []

    with respx.mock(assert_all_called=True) as mock:
        _mock_group_resolution(mock)
        _mock_create_node(mock, captured, echo_root=False)
        _mock_content_and_links(mock)

        result = await _call(server, {
            "name": "Task, backend response omits rootNodeId",
            "content": "scope",
            "project_id": _PROJECT_ID,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    body = _create_body(captured)
    assert body.get("rootNodeId") == _PROJECT_ID
    assert result["rootNodeId"] == _PROJECT_ID, (
        f"Expected the tool to fall back to the locally-computed rootNodeId={_PROJECT_ID} "
        f"when the create response does not echo it back, got: {result!r}"
    )


@pytest.mark.asyncio
async def test_server_echoed_root_node_id_wins_over_computed_value(server: FastMCP) -> None:
    server_value = 424242
    assert server_value != _PROJECT_ID

    with respx.mock(assert_all_called=True) as mock:
        _mock_group_resolution(mock)
        _mock_create_node(mock, [], override_root_node_id=server_value)
        _mock_content_and_links(mock)

        result = await _call(server, {
            "name": "Task, server echoes a different rootNodeId than requested",
            "content": "scope",
            "project_id": _PROJECT_ID,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert result["rootNodeId"] == server_value, (
        f"Expected the tool to report the server's rootNodeId={server_value}, not the "
        f"locally-computed {_PROJECT_ID}, got: {result!r}"
    )


@pytest.mark.asyncio
async def test_explicit_null_echo_is_honoured_as_honest_null(server: FastMCP) -> None:
    with respx.mock(assert_all_called=True) as mock:
        _mock_group_resolution(mock)
        _mock_create_node(mock, [], override_root_node_id=None)
        _mock_content_and_links(mock)

        result = await _call(server, {
            "name": "Task, server explicitly echoes rootNodeId=null",
            "content": "scope",
            "project_id": _PROJECT_ID,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert result["rootNodeId"] is None, (
        f"Expected the tool to report the server's explicit null rather than falling back "
        f"to the locally-computed value, got: {result!r}"
    )
