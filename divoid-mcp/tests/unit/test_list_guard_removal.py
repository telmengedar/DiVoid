"""
Unit tests pinning the removal of client-side guards in list_nodes.py that were
stricter than the backend they wrap.

NodeService.GenerateFilter OR-composes status/nostatus, severity/no_severity, and
root_node_id/no_root_node_id -- each pair reads "matches the list, or has none set"
-- the same way it already OR-composes refinement/norefinement (see
test_refinement_list_search.py). The three mutual-exclusion guards that used to
reject those combinations were removed because nothing distinguished them from
refinement/norefinement, which never had one. These tests are the guards'
mutation pins: re-add any one of the three checks and its test below goes red
alone.

Also pins the removal of the client-side sort allow-list: the node mapper backing
this endpoint registers far more sort keys than the tool used to accept, so the
allow-list is gone and a previously-rejected value now reaches the backend, which
is the only party that actually knows the current key set.

No network calls beyond a respx mock and no live credentials are required.
"""

from __future__ import annotations

from typing import Any

import httpx
import pytest
import respx
from mcp.server.fastmcp import FastMCP

from divoid_mcp import http_client
from divoid_mcp.config import DivoidConfig
from divoid_mcp.tools.list_nodes import register as register_list_nodes

_DUMMY_BASE = "http://divoid.test"
_DUMMY_KEY = "dummy-key-for-unit-tests"
_NODES_URL = f"{_DUMMY_BASE}/nodes"


@pytest.fixture(scope="module")
def server() -> FastMCP:
    """Module-scoped FastMCP server with only divoid_list registered."""
    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY, source="env")
    http_client.init(_DUMMY_BASE, _DUMMY_KEY)

    mcp_server = FastMCP("divoid-mcp-list-guard-removal-test")
    mcp_server.config = config  # type: ignore[attr-defined]
    register_list_nodes(mcp_server)

    return mcp_server


async def _call(server: FastMCP, args: dict[str, Any]) -> dict[str, Any]:
    """Call divoid_list with the given args and return the raw dict."""
    result = await server._tool_manager.call_tool("divoid_list", args)
    assert isinstance(result, dict), f"Expected dict, got {type(result)}"
    return result


def _mock_response(mock: respx.MockRouter, payload: dict[str, Any]) -> list[httpx.Request]:
    """Mock GET /nodes and capture every request made, for param assertions."""
    captured: list[httpx.Request] = []

    def _handler(request: httpx.Request) -> httpx.Response:
        captured.append(request)
        return httpx.Response(200, json=payload)

    mock.get(_NODES_URL).mock(side_effect=_handler)
    return captured


# ---------------------------------------------------------------------------
# The three removed mutual-exclusion guards
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_nostatus_and_status_together_not_rejected(server: FastMCP) -> None:
    """nostatus=True + status=['open'] must NOT raise a mutual-exclusion error --
    the backend OR-composes the combination ("status is in the list, or it is
    unset").

    Substitution probe: re-add a nostatus/status mutual-exclusion check to
    _check_invariants -- this call returns isError and the test fails.
    """
    payload = {"result": [], "total": 0, "continue": None}

    with respx.mock(assert_all_called=True) as mock:
        captured = _mock_response(mock, payload)
        result = await _call(server, {"nostatus": True, "status": ["open"]})

    assert result.get("isError") is not True, (
        f"nostatus + status together must be accepted (OR-composed by the "
        f"backend), got: {result}"
    )
    assert captured[0].url.params.get_list("status") == ["open"]
    assert captured[0].url.params.get("nostatus") == "true"


@pytest.mark.asyncio
async def test_no_severity_and_severity_together_not_rejected(server: FastMCP) -> None:
    """no_severity=True + severity=[5] must NOT raise a mutual-exclusion error --
    the backend OR-composes the combination.

    Substitution probe: re-add a no_severity/severity mutual-exclusion check to
    _check_invariants -- this call returns isError and the test fails.
    """
    payload = {"result": [], "total": 0, "continue": None}

    with respx.mock(assert_all_called=True) as mock:
        captured = _mock_response(mock, payload)
        result = await _call(server, {"no_severity": True, "severity": [5]})

    assert result.get("isError") is not True, (
        f"no_severity + severity together must be accepted (OR-composed by the "
        f"backend), got: {result}"
    )
    assert captured[0].url.params.get_list("severity") == ["5"]
    assert captured[0].url.params.get("noSeverity") == "true"


@pytest.mark.asyncio
async def test_no_root_node_id_and_root_node_id_together_not_rejected(server: FastMCP) -> None:
    """no_root_node_id=True + root_node_id=[5] must NOT raise a mutual-exclusion
    error -- the backend OR-composes the combination.

    Substitution probe: re-add a no_root_node_id/root_node_id mutual-exclusion
    check to _check_invariants -- this call returns isError and the test fails.
    """
    payload = {"result": [], "total": 0, "continue": None}

    with respx.mock(assert_all_called=True) as mock:
        captured = _mock_response(mock, payload)
        result = await _call(server, {"no_root_node_id": True, "root_node_id": [5]})

    assert result.get("isError") is not True, (
        f"no_root_node_id + root_node_id together must be accepted (OR-composed "
        f"by the backend), got: {result}"
    )
    assert captured[0].url.params.get_list("rootNodeId") == ["5"]
    assert captured[0].url.params.get("noRootNodeId") == "true"


# ---------------------------------------------------------------------------
# The removed sort allow-list
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_sort_by_a_field_outside_the_old_allowlist_is_forwarded(server: FastMCP) -> None:
    """sort='created' -- a field the old six-key allow-list never listed -- passes
    straight through instead of being rejected client-side.

    Substitution probe: reintroduce a fixed sort-key allow-list in
    _check_invariants -- this call returns isError and the test fails.
    """
    payload = {"result": [], "total": 0, "continue": None}

    with respx.mock(assert_all_called=True) as mock:
        captured = _mock_response(mock, payload)
        result = await _call(server, {"sort": "created"})

    assert result.get("isError") is not True, (
        f"Expected success for sort='created', got: {result}"
    )
    assert captured[0].url.params.get("sort") == "created"


@pytest.mark.asyncio
async def test_sort_by_lastupdate_is_forwarded(server: FastMCP) -> None:
    """sort='lastupdate' -- another field the old allow-list never listed -- is
    forwarded rather than rejected.

    Substitution probe: reintroduce a fixed sort-key allow-list in
    _check_invariants -- this call returns isError and the test fails.
    """
    payload = {"result": [], "total": 0, "continue": None}

    with respx.mock(assert_all_called=True) as mock:
        captured = _mock_response(mock, payload)
        result = await _call(server, {"sort": "lastupdate", "descending": True})

    assert result.get("isError") is not True, (
        f"Expected success for sort='lastupdate', got: {result}"
    )
    assert captured[0].url.params.get("sort") == "lastupdate"
