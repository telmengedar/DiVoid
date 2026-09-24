"""
Unit tests pinning the removal of divoid_create_task's fixed status allow-list.

The tool used to reject any status outside {"new", "open", "in-progress",
"closed"}. The backend's Status column has no vocabulary check -- it is a
plain string with no allow-list attribute, and node creation inserts it
verbatim -- so the allow-list was stricter than the system it wraps and has
been removed. "open" remains the tool's own default; it is a starting point,
not a constraint on what else is accepted.

These tests are the guard's mutation pin: reintroduce a fixed-status check in
_check_invariants (or in the registered tool) and either test below goes red.

No network calls beyond a respx mock and no live credentials are required.
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
from divoid_mcp.errors import InvariantViolation
from divoid_mcp.tools.create_task import _check_invariants, register as register_create_task

_DUMMY_BASE = "http://divoid.test"
_DUMMY_KEY = "dummy-key-for-unit-tests"

_PROJECT_ID = 42
_TASKS_GROUP_ID = 314
_NEW_NODE_ID = 999

_NODES_URL = f"{_DUMMY_BASE}/nodes"
_CONTENT_URL = f"{_DUMMY_BASE}/nodes/{_NEW_NODE_ID}/content"
_LINKS_URL = f"{_DUMMY_BASE}/nodes/{_NEW_NODE_ID}/links"


@pytest.fixture(scope="module")
def server() -> FastMCP:
    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY, source="env")
    http_client.init(_DUMMY_BASE, _DUMMY_KEY)

    mcp_server = FastMCP("divoid-mcp-create-task-status-test")
    mcp_server.config = config  # type: ignore[attr-defined]
    register_create_task(mcp_server)

    return mcp_server


async def _call(server: FastMCP, args: dict[str, Any]) -> dict[str, Any]:
    result = await server._tool_manager.call_tool("divoid_create_task", args)
    assert isinstance(result, dict), f"Expected dict, got {type(result)}"
    return result


def test_check_invariants_accepts_a_status_outside_the_old_lifecycle() -> None:
    """A status value never in the old {new,open,in-progress,closed} set does not
    raise InvariantViolation.

    Substitution probe: reintroduce a fixed-status allow-list check in
    _check_invariants -- this call raises and the test fails.
    """
    _check_invariants(
        name="Task with a status the old lifecycle never listed",
        content="scope",
        status="triaged",
        project_id=_PROJECT_ID,
        tasks_group_id=None,
    )


@pytest.mark.asyncio
async def test_freeform_status_round_trips_in_post_body(server: FastMCP) -> None:
    """A status value outside the old fixed set reaches the POST /nodes body
    unchanged, and the tool result reports it back.

    Substitution probe: reintroduce a fixed-status allow-list check anywhere on
    this path -- the call returns isError and this test fails.
    """
    captured: list[httpx.Request] = []

    def create(req: httpx.Request) -> httpx.Response:
        captured.append(req)
        body = json.loads(req.content)
        return httpx.Response(200, json={"id": _NEW_NODE_ID, **body})

    with respx.mock(assert_all_called=True) as mock:
        mock.get(_NODES_URL).mock(
            return_value=httpx.Response(200, json={"result": [{"id": _TASKS_GROUP_ID}]})
        )
        mock.post(_NODES_URL).mock(side_effect=create)
        mock.post(_CONTENT_URL).mock(return_value=httpx.Response(200, json={}))
        mock.post(_LINKS_URL).mock(return_value=httpx.Response(200, json={}))

        result = await _call(server, {
            "name": "Task with a status the old lifecycle never listed",
            "content": "scope",
            "project_id": _PROJECT_ID,
            "status": "triaged",
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured) == 1, f"Expected exactly one POST /nodes call, got {len(captured)}"
    body = json.loads(captured[0].content)
    assert body.get("status") == "triaged", (
        f"Expected free-form status to be forwarded verbatim, got POST body: {body!r}"
    )
    assert result["status"] == "triaged"
