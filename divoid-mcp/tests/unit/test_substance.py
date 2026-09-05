"""Unit tests for the substance surface: registered-tool forwarding and the partial_state envelope."""

from __future__ import annotations

import json
from typing import Any

import httpx
import pytest
import respx
from mcp.server.fastmcp import FastMCP

from divoid_mcp import http_client
from divoid_mcp.config import DivoidConfig
from divoid_mcp.tools import create_documentation as create_documentation_module
from divoid_mcp.tools import create_node as create_node_module
from divoid_mcp.tools import create_session_log as create_session_log_module
from divoid_mcp.tools import create_task as create_task_module
from divoid_mcp.tools.create_documentation import register as register_create_documentation
from divoid_mcp.tools.create_node import register as register_create_node
from divoid_mcp.tools.create_session_log import register as register_create_session_log
from divoid_mcp.tools.create_task import register as register_create_task
from divoid_mcp.tools.patch_node import register as register_patch_node

_DUMMY_BASE = "http://divoid.test"
_DUMMY_KEY = "dummy-key-for-unit-tests"

_PROJECT_ID = 3
_DOCS_GROUP_ID = 7
_TASKS_GROUP_ID = 314
_NODE_ID = 12100

_NODES_URL = f"{_DUMMY_BASE}/nodes"
_NODE_URL = f"{_DUMMY_BASE}/nodes/{_NODE_ID}"
_CONTENT_URL = f"{_DUMMY_BASE}/nodes/{_NODE_ID}/content"
_LINKS_URL = f"{_DUMMY_BASE}/nodes/{_NODE_ID}/links"

_SUBSTANCE = "facts only, no prose"


@pytest.fixture(scope="module")
def server() -> FastMCP:
    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)
    http_client.init(_DUMMY_BASE, _DUMMY_KEY)

    mcp_server = FastMCP("divoid-mcp-substance-test")
    mcp_server.config = config  # type: ignore[attr-defined]
    register_patch_node(mcp_server)
    register_create_task(mcp_server)
    register_create_documentation(mcp_server)
    register_create_session_log(mcp_server)
    register_create_node(mcp_server)

    return mcp_server


async def _call(server: FastMCP, tool: str, args: dict[str, Any]) -> dict[str, Any]:
    result = await server._tool_manager.call_tool(tool, args)
    assert isinstance(result, dict), f"Expected dict, got {type(result)}"
    return result


def _mock_patch(mock: respx.MockRouter, captured: list[httpx.Request]) -> None:
    def patch(req: httpx.Request) -> httpx.Response:
        captured.append(req)
        return httpx.Response(200, json={"id": _NODE_ID, "substance": _SUBSTANCE})

    mock.patch(_NODE_URL).mock(side_effect=patch)


def _mock_create_flow(mock: respx.MockRouter) -> None:
    mock.get(_NODES_URL).mock(
        return_value=httpx.Response(200, json={"result": [{"id": _DOCS_GROUP_ID}]})
    )
    mock.post(_NODES_URL).mock(return_value=httpx.Response(200, json={"id": _NODE_ID}))
    mock.post(_CONTENT_URL).mock(return_value=httpx.Response(200, json={}))
    mock.post(_LINKS_URL).mock(return_value=httpx.Response(200, json={}))


def _ops(captured: list[httpx.Request]) -> list[dict[str, Any]]:
    assert len(captured) == 1, f"Expected exactly one PATCH, got {len(captured)}"
    return json.loads(captured[0].content)


@pytest.mark.asyncio
async def test_patch_node_tool_forwards_substance_to_the_patch_body(server: FastMCP) -> None:
    captured: list[httpx.Request] = []

    with respx.mock(assert_all_called=True) as mock:
        _mock_patch(mock, captured)
        result = await _call(server, "divoid_patch_node", {
            "id": _NODE_ID,
            "substance": _SUBSTANCE,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert _ops(captured) == [{"op": "replace", "path": "/substance", "value": _SUBSTANCE}], (
        f"Expected the registered tool to forward substance into the patch body, got: {_ops(captured)!r}"
    )


@pytest.mark.asyncio
async def test_patch_node_tool_forwards_clear_substance_to_the_patch_body(server: FastMCP) -> None:
    captured: list[httpx.Request] = []

    with respx.mock(assert_all_called=True) as mock:
        _mock_patch(mock, captured)
        result = await _call(server, "divoid_patch_node", {
            "id": _NODE_ID,
            "clear_substance": True,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert _ops(captured) == [{"op": "replace", "path": "/substance", "value": None}], (
        f"Expected the registered tool to forward clear_substance, got: {_ops(captured)!r}"
    )


@pytest.mark.asyncio
async def test_create_session_log_tool_forwards_substance_to_a_patch(server: FastMCP) -> None:
    captured: list[httpx.Request] = []

    with respx.mock(assert_all_called=True) as mock:
        _mock_create_flow(mock)
        _mock_patch(mock, captured)
        result = await _call(server, "divoid_create_session_log", {
            "name": "Session-log carrying substance",
            "content": "narrative",
            "project_id": _PROJECT_ID,
            "substance": _SUBSTANCE,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert _ops(captured) == [{"op": "replace", "path": "/substance", "value": _SUBSTANCE}], (
        f"Expected the registered tool to forward substance into a PATCH, got: {_ops(captured)!r}"
    )


@pytest.mark.asyncio
async def test_create_session_log_without_substance_issues_no_patch(server: FastMCP) -> None:
    with respx.mock(assert_all_called=False) as mock:
        _mock_create_flow(mock)
        patch_route = mock.patch(_NODE_URL).mock(return_value=httpx.Response(200, json={}))

        result = await _call(server, "divoid_create_session_log", {
            "name": "Session-log without substance",
            "content": "narrative",
            "project_id": _PROJECT_ID,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert not patch_route.called, "Expected no PATCH when the caller supplied no substance"


@pytest.mark.asyncio
async def test_failed_substance_write_returns_a_readable_partial_state(server: FastMCP) -> None:
    with respx.mock(assert_all_called=True) as mock:
        _mock_create_flow(mock)
        mock.patch(_NODE_URL).mock(
            return_value=httpx.Response(404, json={"detail": "node not found"})
        )

        result = await _call(server, "divoid_create_session_log", {
            "name": "Session-log whose substance write fails",
            "content": "narrative",
            "project_id": _PROJECT_ID,
            "substance": _SUBSTANCE,
        })

    assert result.get("isError") is True, f"Expected an error envelope, got: {result}"
    text = result["content"][0]["text"]

    assert text.startswith("partial_state: "), f"Expected a partial_state code, got: {text!r}"
    assert "node_not_found" in text, f"Expected the mapped error code in the message, got: {text!r}"
    assert "[{" not in text and "'type':" not in text, (
        f"Expected a plain-string detail, not an embedded content list, got: {text!r}"
    )
    assert f"PATCH /api/nodes/{_NODE_ID} replace /substance" in text, (
        f"Expected a repair instruction naming the node, got: {text!r}"
    )
    assert "its links made" in text, (
        f"Expected the envelope to state the node is otherwise complete, got: {text!r}"
    )


@pytest.mark.asyncio
async def test_failed_substance_write_does_not_leak_the_api_key(server: FastMCP) -> None:
    with respx.mock(assert_all_called=True) as mock:
        _mock_create_flow(mock)
        mock.patch(_NODE_URL).mock(
            return_value=httpx.Response(500, text=f"boom {_DUMMY_KEY}")
        )

        result = await _call(server, "divoid_create_session_log", {
            "name": "Session-log whose substance write fails with a leaky body",
            "content": "narrative",
            "project_id": _PROJECT_ID,
            "substance": _SUBSTANCE,
        })

    assert result.get("isError") is True, f"Expected an error envelope, got: {result}"
    text = result["content"][0]["text"]
    assert _DUMMY_KEY not in text, f"Expected the api key to be redacted, got: {text!r}"


_CREATORS = [
    ("divoid_create_task",
     {"name": "Task whose substance write fails", "content": "body",
      "tasks_group_id": _TASKS_GROUP_ID}),
    ("divoid_create_documentation",
     {"name": "Doc whose substance write fails", "content": "body",
      "docs_group_id": _DOCS_GROUP_ID}),
    ("divoid_create_session_log",
     {"name": "Session-log whose substance write fails", "content": "body",
      "docs_group_id": _DOCS_GROUP_ID}),
    ("divoid_create_node",
     {"name": "Node whose substance write fails", "type": "meeting", "content": "body",
      "extra_links": [_DOCS_GROUP_ID]}),
]


@pytest.mark.parametrize("tool,args", _CREATORS, ids=[name for name, _ in _CREATORS])
@pytest.mark.asyncio
async def test_creator_surfaces_a_failed_substance_write(
    server: FastMCP, tool: str, args: dict[str, Any]
) -> None:
    with respx.mock(assert_all_called=True) as mock:
        mock.post(_NODES_URL).mock(return_value=httpx.Response(200, json={"id": _NODE_ID}))
        mock.post(_CONTENT_URL).mock(return_value=httpx.Response(200, json={}))
        mock.post(_LINKS_URL).mock(return_value=httpx.Response(200, json={}))
        mock.patch(_NODE_URL).mock(
            return_value=httpx.Response(404, json={"detail": "node not found"})
        )

        result = await _call(server, tool, dict(args, substance=_SUBSTANCE))

    assert result.get("isError") is True, (
        f"{tool} reported success despite a failed substance write: {result}"
    )
    text = result["content"][0]["text"]
    assert text.startswith("partial_state: "), f"{tool}: expected partial_state, got: {text!r}"
    assert f"PATCH /api/nodes/{_NODE_ID} replace /substance" in text, (
        f"{tool}: expected a repair instruction naming the node, got: {text!r}"
    )


_DESCRIPTION_MODULES = [
    ("divoid_create_task", create_task_module),
    ("divoid_create_documentation", create_documentation_module),
    ("divoid_create_session_log", create_session_log_module),
    ("divoid_create_node", create_node_module),
]


@pytest.mark.parametrize("label,module", _DESCRIPTION_MODULES, ids=[n for n, _ in _DESCRIPTION_MODULES])
def test_tool_description_lists_substance_among_the_composite_steps(label: str, module: Any) -> None:
    description = module._TOOL_DESCRIPTION
    marker = "all in one call"
    assert marker in description, f"{label}: expected a step enumeration, got: {description!r}"

    steps = description[: description.index(marker)]
    assert "substance" in steps, (
        f"{label}: the shipped step enumeration omits the substance step: {steps!r}"
    )


def test_create_node_partial_failure_enumeration_names_substance() -> None:
    description = create_node_module._TOOL_DESCRIPTION
    start = description.index("On partial failure")
    end = description.index("naming the surviving node id")
    sentence = description[start:end]
    assert "substance" in sentence, (
        f"the shipped partial-failure enumeration omits the substance step: {sentence!r}"
    )
