"""
Unit tests for `refinement` / `norefinement` on divoid_list and divoid_search.

The queue-scan chain is the point of the whole feature (per the design this
mirrors): a tool that accepts `refinement` on create but never returns it in a
list/search response is useless for the query the field exists to answer
("give me the startable open tasks"). These tests pin the two places that chain
would silently break:

  - list_nodes._DEFAULT_FIELDS -- the base fields list used when include_content/
    include_links/include_link_details forces an explicit `fields` param and the
    caller did not supply one. Drop 'refinement' from this list and
    test_list_default_fields_chain_carries_refinement goes red ALONE (no other
    test in this module or test_list_nodes.py depends on that one entry).
  - search.py's base_fields list (same shape, same failure mode) and the
    unconditional 'refinement': n.get('refinement') row projection.

Also covers: refinement/norefinement forwarded as query params, sort='refinement'
accepted by the invariant guard, and -- explicitly -- that refinement +
norefinement together do NOT raise a mutual-exclusion error (unlike
status/nostatus), because the backend OR-composes that combination.

Fixture values ('ready', 'needs-input') are synthetic placeholders illustrating
an open vocabulary, not an enforced list.

No network calls and no DiVoid credentials are required.
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
from divoid_mcp.tools.search import register as register_search

_DUMMY_BASE = "http://divoid.test"
_DUMMY_KEY = "dummy-key-for-unit-tests"
_NODES_URL = f"{_DUMMY_BASE}/nodes"


def _make_server(register_fn, name: str) -> FastMCP:
    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY, source="env")
    http_client.init(_DUMMY_BASE, _DUMMY_KEY)
    mcp_server = FastMCP(name)
    mcp_server.config = config  # type: ignore[attr-defined]
    register_fn(mcp_server)
    return mcp_server


async def _call(server: FastMCP, tool: str, args: dict[str, Any]) -> dict[str, Any]:
    result = await server._tool_manager.call_tool(tool, args)
    assert isinstance(result, dict), f"Expected dict, got {type(result)}"
    return result


def _mock_nodes(mock: respx.MockRouter, payload: dict[str, Any]) -> list[httpx.Request]:
    captured: list[httpx.Request] = []

    def _handler(request: httpx.Request) -> httpx.Response:
        captured.append(request)
        return httpx.Response(200, json=payload)

    mock.get(_NODES_URL).mock(side_effect=_handler)
    return captured


# ---------------------------------------------------------------------------
# divoid_list -- the default-fields chain (the queue-scan mutation target)
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_list_default_fields_chain_carries_refinement() -> None:
    """include_content=True with no explicit fields -> the composed fields list
    (built from _DEFAULT_FIELDS) includes 'refinement'.

    Note: since default-on labelled-edge enrichment (DiVoid #7217), a plain
    flag-less divoid_list call ALSO materializes _DEFAULT_FIELDS into an
    explicit 'fields' param (see test_list_default_call_carries_refinement_in_
    forced_fields below) -- the discriminating claim above ("only include_*
    branches send fields") predates that change and no longer holds, but this
    test still independently pins the same _DEFAULT_FIELDS entry via the
    include_content branch.

    Substitution probe: remove 'refinement' from _DEFAULT_FIELDS in
    list_nodes.py -- this test goes red (as does the plain-call test below).
    """
    server = _make_server(register_list_nodes, "divoid-mcp-refinement-list-default-fields")
    payload = {
        "result": [
            {"id": 1, "type": "task", "name": "n1", "status": "open", "refinement": "ready",
             "content": "body"}
        ],
        "total": 1,
        "continue": None,
    }

    with respx.mock(assert_all_called=True) as mock:
        captured = _mock_nodes(mock, payload)
        result = await _call(server, "divoid_list", {"include_content": True})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    sent_fields = captured[0].url.params.get_list("fields")
    assert "refinement" in sent_fields, (
        f"Expected 'refinement' in the composed fields list, got: {sent_fields!r}"
    )


@pytest.mark.asyncio
async def test_list_default_call_carries_refinement_in_forced_fields() -> None:
    """A plain call (no include_* flags) DOES send a 'fields' param -- divoid_list's
    default-on labelled-edge enrichment (DiVoid #7217) unconditionally appends
    linkDetails/id to the projection regardless of flags, so 'fields' is never
    omitted from the wire request. This supersedes the older assumption (true
    before #7217 landed) that a flag-less call sent no 'fields' at all.

    What this test pins for the refinement feature: 'refinement' rides along in
    that forced fields list -- adding it to _DEFAULT_FIELDS must not require an
    include_* flag to reach the wire.

    Substitution probe: remove 'refinement' from _DEFAULT_FIELDS in
    list_nodes.py -- this test goes red.
    """
    server = _make_server(register_list_nodes, "divoid-mcp-refinement-list-plain")
    payload = {"result": [], "total": 0, "continue": None}

    with respx.mock(assert_all_called=True) as mock:
        captured = _mock_nodes(mock, payload)
        result = await _call(server, "divoid_list", {})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    sent_fields = captured[0].url.params.get_list("fields")
    assert "refinement" in sent_fields, (
        f"Expected 'refinement' in the default forced fields list, got: {sent_fields!r}"
    )


@pytest.mark.asyncio
async def test_list_refinement_filter_forwarded() -> None:
    """refinement=['ready'] -> forwarded as ?refinement=ready.

    Substitution probe: remove `if refinement: params["refinement"] = refinement`
    from list_nodes._execute -- this test fails.
    """
    server = _make_server(register_list_nodes, "divoid-mcp-refinement-list-filter")
    payload = {"result": [], "total": 0, "continue": None}

    with respx.mock(assert_all_called=True) as mock:
        captured = _mock_nodes(mock, payload)
        result = await _call(server, "divoid_list", {"refinement": ["ready"]})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    sent = captured[0].url.params.get_list("refinement")
    assert sent == ["ready"], f"Expected refinement=ready forwarded, got: {sent!r}"


@pytest.mark.asyncio
async def test_list_norefinement_forwarded() -> None:
    """norefinement=True -> forwarded as ?norefinement=true.

    Substitution probe: remove `if norefinement: params["norefinement"] = "true"`
    -- this test fails.
    """
    server = _make_server(register_list_nodes, "divoid-mcp-refinement-list-norefinement")
    payload = {"result": [], "total": 0, "continue": None}

    with respx.mock(assert_all_called=True) as mock:
        captured = _mock_nodes(mock, payload)
        result = await _call(server, "divoid_list", {"norefinement": True})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert captured[0].url.params.get("norefinement") == "true"


@pytest.mark.asyncio
async def test_list_refinement_and_norefinement_together_not_rejected() -> None:
    """refinement=[...] AND norefinement=True together must NOT raise a
    mutual-exclusion invariant error -- unlike status/nostatus, the backend
    OR-composes this combination as a meaningful query.

    Substitution probe: add a mutual-exclusion guard for refinement/norefinement
    (mirroring the nostatus/status one) -- this test starts failing with
    isError=True, which is exactly the regression this test exists to catch.
    """
    server = _make_server(register_list_nodes, "divoid-mcp-refinement-list-both")
    payload = {"result": [], "total": 0, "continue": None}

    with respx.mock(assert_all_called=True) as mock:
        captured = _mock_nodes(mock, payload)
        result = await _call(server, "divoid_list", {
            "refinement": ["ready"], "norefinement": True,
        })

    assert result.get("isError") is not True, (
        f"refinement + norefinement together must be accepted (OR-composed by the backend), "
        f"got: {result}"
    )
    assert captured[0].url.params.get_list("refinement") == ["ready"]
    assert captured[0].url.params.get("norefinement") == "true"


@pytest.mark.asyncio
async def test_list_sort_by_refinement_accepted() -> None:
    """sort='refinement' must pass the invariant guard and be forwarded.

    Substitution probe: remove 'refinement' from _VALID_SORT_FIELDS -- this call
    raises sort_invalid_field and the isError assertion fails.
    """
    server = _make_server(register_list_nodes, "divoid-mcp-refinement-list-sort")
    payload = {"result": [], "total": 0, "continue": None}

    with respx.mock(assert_all_called=True) as mock:
        captured = _mock_nodes(mock, payload)
        result = await _call(server, "divoid_list", {"sort": "refinement"})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert captured[0].url.params.get("sort") == "refinement"


# ---------------------------------------------------------------------------
# divoid_search -- base_fields chain and default row projection
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_search_base_fields_chain_carries_refinement() -> None:
    """include_content=True on divoid_search -> the explicit fields param sent
    includes 'refinement'.

    Substitution probe: remove 'refinement' from search.py's base_fields list --
    this test goes red alone.
    """
    server = _make_server(register_search, "divoid-mcp-refinement-search-base-fields")
    payload = {
        "result": [{"id": 1, "type": "task", "name": "n1", "similarity": 0.9,
                    "refinement": "ready", "content": "body"}],
        "total": 1,
    }

    with respx.mock(assert_all_called=True) as mock:
        captured = _mock_nodes(mock, payload)
        result = await _call(server, "divoid_search", {
            "query": "test", "include_content": True,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    sent_fields = captured[0].url.params.get_list("fields")
    assert "refinement" in sent_fields, (
        f"Expected 'refinement' in composed fields, got: {sent_fields!r}"
    )


@pytest.mark.asyncio
async def test_search_result_row_carries_refinement_when_present() -> None:
    """A backend row with refinement='ready' -> the result row carries refinement='ready'.

    Substitution probe: remove the `"refinement": n.get("refinement")` line from
    search.py's row construction -- this test fails (KeyError-shaped None mismatch).
    """
    server = _make_server(register_search, "divoid-mcp-refinement-search-row")
    payload = {
        "result": [{"id": 1, "type": "task", "name": "n1", "status": "open",
                    "refinement": "ready", "similarity": 0.9}],
        "total": 1,
    }

    with respx.mock(assert_all_called=True) as mock:
        _mock_nodes(mock, payload)
        result = await _call(server, "divoid_search", {"query": "test"})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    row = result["results"][0]
    assert "refinement" in row, f"Expected 'refinement' key present, got: {row!r}"
    assert row["refinement"] == "ready"


@pytest.mark.asyncio
async def test_search_result_row_refinement_null_when_absent() -> None:
    """A backend row with no refinement (structural/group node) -> row carries refinement=None,
    not an exception and not an absent key."""
    server = _make_server(register_search, "divoid-mcp-refinement-search-row-null")
    payload = {
        "result": [{"id": 314, "type": None, "name": "Tasks", "status": None,
                    "similarity": 0.5}],
        "total": 1,
    }

    with respx.mock(assert_all_called=True) as mock:
        _mock_nodes(mock, payload)
        result = await _call(server, "divoid_search", {"query": "tasks group"})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    row = result["results"][0]
    assert row.get("refinement") is None


@pytest.mark.asyncio
async def test_search_refinement_filter_forwarded() -> None:
    """refinement=['needs-input'] on divoid_search -> forwarded as ?refinement=needs-input.

    Substitution probe: remove `if refinement: params["refinement"] = refinement`
    from search.py -- this test fails.
    """
    server = _make_server(register_search, "divoid-mcp-refinement-search-filter")
    payload = {"result": [], "total": 0}

    with respx.mock(assert_all_called=True) as mock:
        captured = _mock_nodes(mock, payload)
        result = await _call(server, "divoid_search", {
            "query": "test", "refinement": ["needs-input"],
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    sent = captured[0].url.params.get_list("refinement")
    assert sent == ["needs-input"], f"Expected refinement forwarded, got: {sent!r}"


@pytest.mark.asyncio
async def test_search_no_refinement_param_when_omitted() -> None:
    """Regression: omitting refinement on divoid_search must not send the param at all."""
    server = _make_server(register_search, "divoid-mcp-refinement-search-omitted")
    payload = {"result": [], "total": 0}

    with respx.mock(assert_all_called=True) as mock:
        captured = _mock_nodes(mock, payload)
        result = await _call(server, "divoid_search", {"query": "test"})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert "refinement" not in captured[0].url.params
