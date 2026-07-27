"""
Unit tests for divoid_get_links: link_type + context surfaced on read (DiVoid #7147).

Read-side complement to test_link_nodes.py's write-side #7138 coverage. These
tests mock the HTTP transport (via respx) and assert on the exact result rows
divoid_get_links produces from a given backend JSON payload:

  - Backend row carries linkType/context -> result row has link_type/context,
    pass-through (no vocabulary policing — divoid-mcp CLAUDE.md invariant 6).
  - Backend row carries context: null -> result row has context: None (the key
    IS present; the backend explicitly said "no context").
  - Backend row omits linkType/context entirely (pre-#163 backend) -> result
    row omits both keys too (not surfaced as null) — this is the back-compat
    contract from DiVoid #7147.
  - Existing source_id/target_id camelCase->snake_case normalization is
    unchanged by this change.

No network calls and no DiVoid credentials are required.

Architecture reference: DiVoid #7119/#163 (backend), #7138 (write-side tool),
#7147 (this read-side tool).
"""

from __future__ import annotations

from typing import Any

import httpx
import pytest
import respx
from mcp.server.fastmcp import FastMCP

from divoid_mcp import http_client
from divoid_mcp.config import DivoidConfig
from divoid_mcp.tools.get_links import register as register_get_links

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

_DUMMY_BASE = "http://divoid.test"
_DUMMY_KEY = "dummy-key-for-unit-tests"

_LINKS_URL = f"{_DUMMY_BASE}/nodes/links"


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture(scope="module")
def server() -> FastMCP:
    """Module-scoped FastMCP server with only divoid_get_links registered."""
    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)
    http_client.init(_DUMMY_BASE, _DUMMY_KEY)

    mcp_server = FastMCP("divoid-mcp-get-links-test")
    mcp_server.config = config  # type: ignore[attr-defined]
    register_get_links(mcp_server)

    return mcp_server


async def _call(server: FastMCP, args: dict[str, Any]) -> dict[str, Any]:
    """Call divoid_get_links with the given args and return the raw dict."""
    result = await server._tool_manager.call_tool("divoid_get_links", args)
    assert isinstance(result, dict), f"Expected dict, got {type(result)}"
    return result


def _mock_response(mock: respx.MockRouter, payload: dict[str, Any]) -> None:
    mock.get(_LINKS_URL).mock(return_value=httpx.Response(200, json=payload))


# ---------------------------------------------------------------------------
# Backend row carries linkType + context -> pass through as link_type/context
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_link_type_and_context_surfaced(server: FastMCP) -> None:
    """A backend row with linkType/context set produces link_type/context in the result.

    Substitution probe: reverting the normalization to only source_id/target_id
    (the pre-#7147 shape) would drop link_type/context from the row entirely —
    this assertion fails in that case.
    """
    payload = {
        "result": [
            {
                "sourceId": 10,
                "targetId": 20,
                "linkType": "Unidirectional",
                "context": "subtask",
            }
        ],
        "total": 1,
        "continue": None,
    }

    with respx.mock(assert_all_called=True) as mock:
        _mock_response(mock, payload)
        result = await _call(server, {"ids": [10]})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    rows = result["result"]
    assert len(rows) == 1
    row = rows[0]
    assert row == {
        "source_id": 10,
        "target_id": 20,
        "link_type": "Unidirectional",
        "context": "subtask",
    }, f"Unexpected row shape: {row!r}"


# ---------------------------------------------------------------------------
# Backend row carries context: null -> result has context: None (key present)
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_null_context_is_surfaced_as_none_not_omitted(server: FastMCP) -> None:
    """context explicitly null in the backend row -> row['context'] is None.

    This is distinct from the field being absent entirely (see the next test):
    the backend told us there is no context, vs. the backend not knowing about
    the field at all. Substitution probe: treating `.get("context")` presence
    the same as `link.get("context") is not None` would collapse this case
    into the "omit" branch and fail this assertion.
    """
    payload = {
        "result": [
            {
                "sourceId": 10,
                "targetId": 20,
                "linkType": "None",
                "context": None,
            }
        ],
        "total": 1,
        "continue": None,
    }

    with respx.mock(assert_all_called=True) as mock:
        _mock_response(mock, payload)
        result = await _call(server, {"ids": [10]})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    row = result["result"][0]
    assert "context" in row, f"context key must be present (not omitted), got: {row!r}"
    assert row["context"] is None, f"Expected context=None, got: {row['context']!r}"
    assert row["link_type"] == "None"


# ---------------------------------------------------------------------------
# Backend row omits linkType/context entirely (older backend, pre-#163) ->
# result row omits both keys too, rather than fabricating a null.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_missing_fields_are_omitted_not_fabricated(server: FastMCP) -> None:
    """A backend row without linkType/context keys -> result row has neither key.

    Back-compat contract (DiVoid #7147): a pre-#163 backend response simply
    doesn't carry these fields. Substitution probe: using `link.get("linkType")`
    unconditionally (instead of checking `"linkType" in link` first) would
    fabricate `link_type: None` here — this assertion fails in that case,
    which is exactly the bug this back-compat contract guards against.
    """
    payload = {
        "result": [
            {
                "sourceId": 10,
                "targetId": 20,
            }
        ],
        "total": 1,
        "continue": None,
    }

    with respx.mock(assert_all_called=True) as mock:
        _mock_response(mock, payload)
        result = await _call(server, {"ids": [10]})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    row = result["result"][0]
    assert row == {"source_id": 10, "target_id": 20}, (
        f"Expected only source_id/target_id (no fabricated link_type/context), got: {row!r}"
    )
    assert "link_type" not in row
    assert "context" not in row


# ---------------------------------------------------------------------------
# Existing source_id/target_id normalization is unchanged (regression guard)
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_source_target_id_normalization_unchanged(server: FastMCP) -> None:
    """sourceId/targetId still normalize to source_id/target_id, multi-row.

    Regression guard predating #7147 — must survive the link_type/context
    addition unchanged.
    """
    payload = {
        "result": [
            {"sourceId": 1, "targetId": 2},
            {"sourceId": 3, "targetId": 4, "linkType": "Bidirectional", "context": "ref"},
        ],
        "total": 2,
        "continue": None,
    }

    with respx.mock(assert_all_called=True) as mock:
        _mock_response(mock, payload)
        result = await _call(server, {"ids": [1, 3]})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    rows = result["result"]
    assert rows[0] == {"source_id": 1, "target_id": 2}
    assert rows[1] == {
        "source_id": 3,
        "target_id": 4,
        "link_type": "Bidirectional",
        "context": "ref",
    }
    assert result["total"] == 2


# ---------------------------------------------------------------------------
# Existing structural guard must still fire before any HTTP call
# (regression guard predating #7147 — must survive this change)
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_empty_ids_rejected_before_http(server: FastMCP) -> None:
    """ids=[] -> isError, no HTTP call."""
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json={"result": [], "total": 0, "continue": None})

        mock.get(_LINKS_URL).mock(side_effect=detect)

        result = await _call(server, {"ids": []})

    assert result.get("isError") is True, f"Expected isError=True for empty ids, got: {result}"
    assert not http_called, "HTTP must NOT be called when ids is empty"
    text = result["content"][0]["text"]
    assert "ids_empty" in text, f"Expected 'ids_empty' code, got: {text!r}"
