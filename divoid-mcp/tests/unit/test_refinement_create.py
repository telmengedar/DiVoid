"""
Unit tests for `refinement` pass-through on the four composite create tools:
divoid_create_node, divoid_create_task, divoid_create_documentation,
divoid_create_session_log.

`refinement` is an open-vocabulary string answering "how settled is this node's
own content?", independent of `status`. The tool layer does no validation on it —
it is a straight pass-through, mirroring how `severity` already behaves. These
tests assert the POST /nodes body carries the value when given and omits the key
entirely when it is not (absence, not a coerced default, mirroring severity).

divoid_create_documentation is the load-bearing case: unlike the other three
creators it has no `status` parameter at all, so it is the one tool where
`refinement` is the ONLY node-lifecycle-adjacent scalar available. A regression
that drops the parameter from this tool alone (while keeping it on the other
three) would go undetected by any test that only exercises create_task/create_node.

Fixture values ('ready', 'in-review') are synthetic placeholders, not members of
an enforced vocabulary -- the tool accepts any string.

No network calls and no DiVoid credentials are required.
"""

from __future__ import annotations

import json
from typing import Any

import httpx
import pytest
import respx
from mcp.server.fastmcp import FastMCP

from divoid_mcp import http_client
from divoid_mcp.config import DivoidConfig
from divoid_mcp.tools.create_documentation import register as register_create_documentation
from divoid_mcp.tools.create_node import register as register_create_node
from divoid_mcp.tools.create_session_log import register as register_create_session_log
from divoid_mcp.tools.create_task import register as register_create_task

_DUMMY_BASE = "http://divoid.test"
_DUMMY_KEY = "dummy-key-for-unit-tests"
_NODES_URL = f"{_DUMMY_BASE}/nodes"
_NEW_NODE_ID = 4242
_CONTENT_URL = f"{_DUMMY_BASE}/nodes/{_NEW_NODE_ID}/content"
_LINKS_URL = f"{_DUMMY_BASE}/nodes/{_NEW_NODE_ID}/links"
_GROUP_ID = 314


def _make_server(register_fn) -> FastMCP:
    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY, source="env")
    http_client.init(_DUMMY_BASE, _DUMMY_KEY)
    mcp_server = FastMCP(f"divoid-mcp-refinement-{register_fn.__module__}")
    mcp_server.config = config  # type: ignore[attr-defined]
    register_fn(mcp_server)
    return mcp_server


async def _call(server: FastMCP, tool: str, args: dict[str, Any]) -> dict[str, Any]:
    result = await server._tool_manager.call_tool(tool, args)
    assert isinstance(result, dict), f"Expected dict, got {type(result)}"
    return result


def _mock_group_resolution(mock: respx.MockRouter) -> None:
    mock.get(_NODES_URL).mock(
        return_value=httpx.Response(200, json={"result": [{"id": _GROUP_ID}]})
    )


def _mock_create_and_children(mock: respx.MockRouter, captured: list[httpx.Request]) -> None:
    def create(req: httpx.Request) -> httpx.Response:
        captured.append(req)
        body = json.loads(req.content)
        return httpx.Response(201, json={"id": _NEW_NODE_ID, **body})

    mock.post(_NODES_URL).mock(side_effect=create)
    mock.post(_CONTENT_URL).mock(return_value=httpx.Response(200, json={}))
    mock.post(_LINKS_URL).mock(return_value=httpx.Response(200, json={}))


def _create_body(captured: list[httpx.Request]) -> dict[str, Any]:
    assert len(captured) == 1, f"Expected exactly one POST /nodes call, got {len(captured)}"
    return json.loads(captured[0].content)


# ---------------------------------------------------------------------------
# divoid_create_node
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_create_node_with_refinement_included_in_post_body() -> None:
    """refinement='ready' on divoid_create_node -> POST /nodes body carries it.

    Substitution probe: remove the `node_body["refinement"] = refinement` line
    from create_node.py -- this test fails because the key is absent from body.
    """
    server = _make_server(register_create_node)
    captured: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        _mock_create_and_children(mock, captured)
        result = await _call(server, "divoid_create_node", {
            "name": "generic node",
            "refinement": "ready",
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    body = _create_body(captured)
    assert body.get("refinement") == "ready", (
        f"Expected refinement='ready' in POST body, got: {body!r}"
    )


@pytest.mark.asyncio
async def test_create_node_without_refinement_key_absent_from_post_body() -> None:
    """Omitting refinement -> no 'refinement' key at all in the POST body (not null)."""
    server = _make_server(register_create_node)
    captured: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        _mock_create_and_children(mock, captured)
        result = await _call(server, "divoid_create_node", {"name": "generic node"})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    body = _create_body(captured)
    assert "refinement" not in body, (
        f"Expected no 'refinement' key when omitted, got: {body!r}"
    )


@pytest.mark.asyncio
async def test_create_node_echoes_refinement_in_result_summary() -> None:
    """divoid_create_node's returned summary echoes refinement, mirroring status.

    §9's create_node row calls this out specifically as a "summary echo" -- a
    straight mirror of how `status` is already echoed in the same dict.

    Substitution probe: remove the `"refinement": refinement` line from the
    return dict in create_node.py -- this test fails because the key is absent.
    """
    server = _make_server(register_create_node)
    captured: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        _mock_create_and_children(mock, captured)
        result = await _call(server, "divoid_create_node", {
            "name": "generic node",
            "status": "open",
            "refinement": "ready",
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert "refinement" in result, (
        f"Expected 'refinement' key in result summary, got keys: {list(result.keys())!r}. "
        "Substitution probe: removing 'refinement' from create_node's return dict causes this failure."
    )
    assert result.get("refinement") == "ready", (
        f"Expected refinement='ready' echoed in result, got: {result.get('refinement')!r}"
    )
    assert result.get("status") == "open", "status echo must remain unaffected by the refinement echo"


@pytest.mark.asyncio
async def test_create_node_without_refinement_echoes_none_in_result_summary() -> None:
    """Omitting refinement -> result summary carries refinement=None (key present, mirrors status)."""
    server = _make_server(register_create_node)
    captured: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        _mock_create_and_children(mock, captured)
        result = await _call(server, "divoid_create_node", {"name": "generic node"})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert "refinement" in result, (
        f"Expected 'refinement' key present even when omitted, got keys: {list(result.keys())!r}"
    )
    assert result.get("refinement") is None, (
        f"Expected refinement=None when omitted, got: {result.get('refinement')!r}"
    )


# ---------------------------------------------------------------------------
# divoid_create_task
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_create_task_with_refinement_included_in_post_body() -> None:
    """refinement on divoid_create_task -> POST /nodes body carries it, alongside status.

    Substitution probe: remove the `node_body["refinement"] = refinement` line
    from create_task.py -- this test fails.
    """
    server = _make_server(register_create_task)
    captured: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        _mock_group_resolution(mock)
        _mock_create_and_children(mock, captured)
        result = await _call(server, "divoid_create_task", {
            "name": "a task",
            "project_id": 1,
            "content": "scope",
            "refinement": "needs-investigation",
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    body = _create_body(captured)
    assert body.get("refinement") == "needs-investigation"
    assert body.get("status") == "open", "status must still be set independently of refinement"


@pytest.mark.asyncio
async def test_create_task_status_new_without_content_still_works_with_refinement() -> None:
    """status='new' quick-capture path, which waives the content requirement, is untouched by refinement."""
    server = _make_server(register_create_task)
    captured: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        _mock_group_resolution(mock)
        _mock_create_and_children(mock, captured)
        result = await _call(server, "divoid_create_task", {
            "name": "a quick capture",
            "project_id": 1,
            "status": "new",
            "refinement": "needs-investigation",
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    body = _create_body(captured)
    assert body.get("status") == "new"
    assert body.get("refinement") == "needs-investigation"


# ---------------------------------------------------------------------------
# divoid_create_documentation -- the load-bearing case (no status parameter at all)
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_create_documentation_with_refinement_included_in_post_body() -> None:
    """refinement on divoid_create_documentation -> POST /nodes body carries it.

    This is the load-bearing tool: create_documentation has no status parameter,
    so refinement is the only node-lifecycle-adjacent scalar it can carry. If this
    parameter were missing, the design's generality claim would be false regardless
    of what the other three creators do.

    Substitution probe: remove the `node_body["refinement"] = refinement` line
    from create_documentation.py -- this test fails alone (the other three
    creators' equivalent tests keep passing).
    """
    server = _make_server(register_create_documentation)
    captured: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        _mock_group_resolution(mock)
        _mock_create_and_children(mock, captured)
        result = await _call(server, "divoid_create_documentation", {
            "name": "a design doc",
            "project_id": 1,
            "content": "the document body",
            "refinement": "draft",
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    body = _create_body(captured)
    assert body.get("refinement") == "draft", (
        f"Expected refinement='draft' in POST body, got: {body!r}"
    )
    assert "status" not in body, (
        "documentation nodes carry no status -- refinement must not have smuggled one in"
    )


@pytest.mark.asyncio
async def test_create_documentation_without_refinement_key_absent() -> None:
    """Omitting refinement on divoid_create_documentation -> key absent, content still required."""
    server = _make_server(register_create_documentation)
    captured: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        _mock_group_resolution(mock)
        _mock_create_and_children(mock, captured)
        result = await _call(server, "divoid_create_documentation", {
            "name": "a design doc",
            "project_id": 1,
            "content": "the document body",
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    body = _create_body(captured)
    assert "refinement" not in body


# ---------------------------------------------------------------------------
# divoid_create_session_log
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_create_session_log_with_refinement_included_in_post_body() -> None:
    """refinement on divoid_create_session_log -> POST /nodes body carries it.

    Substitution probe: remove the `node_body["refinement"] = refinement` line
    from create_session_log.py's _execute -- this test fails.
    """
    server = _make_server(register_create_session_log)
    captured: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        _mock_group_resolution(mock)
        _mock_create_and_children(mock, captured)
        result = await _call(server, "divoid_create_session_log", {
            "name": "a session log",
            "project_id": 1,
            "content": "the narrative",
            "refinement": "in-review",
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    body = _create_body(captured)
    assert body.get("refinement") == "in-review"
