"""
Unit tests for divoid_list's include_link_details flag (DiVoid #7163).

Mirrors the include_links precedent and the divoid_get_links normalization
convention (see test_get_links.py): these tests mock the HTTP transport (via
respx) and assert on both the outbound `fields` query param and the exact
result rows divoid_list produces from a given backend JSON payload.

  - Flag off (default) -> no 'linkDetails' appended to fields, no
    'link_details' key in any result row. Byte-identical back-compat with
    pre-#7163 behavior.
  - Flag on -> 'linkDetails' appended to the fields projection; each row's
    raw 'linkDetails' array is popped and replaced with normalized
    'link_details' rows (source_id/target_id always present; link_type/
    context pass-through, surfaced only when the backend row carries them).
  - Composes with include_links: both 'links' and 'linkDetails' appended to
    fields; both 'links' (untouched passthrough) and 'link_details'
    (normalized) present on the row.

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

_DUMMY_BASE = "http://divoid.test"
_DUMMY_KEY = "dummy-key-for-unit-tests"

_NODES_URL = f"{_DUMMY_BASE}/nodes"


@pytest.fixture(scope="module")
def server() -> FastMCP:
    """Module-scoped FastMCP server with only divoid_list registered."""
    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)
    http_client.init(_DUMMY_BASE, _DUMMY_KEY)

    mcp_server = FastMCP("divoid-mcp-list-nodes-test")
    mcp_server.config = config  # type: ignore[attr-defined]
    register_list_nodes(mcp_server)

    return mcp_server


async def _call(server: FastMCP, args: dict[str, Any]) -> dict[str, Any]:
    """Call divoid_list with the given args and return the raw dict."""
    result = await server._tool_manager.call_tool("divoid_list", args)
    assert isinstance(result, dict), f"Expected dict, got {type(result)}"
    return result


def _mock_response(
    mock: respx.MockRouter, payload: dict[str, Any]
) -> list[httpx.Request]:
    """Mock GET /nodes and capture every request made, for param assertions."""
    captured: list[httpx.Request] = []

    def _handler(request: httpx.Request) -> httpx.Response:
        captured.append(request)
        return httpx.Response(200, json=payload)

    mock.get(_NODES_URL).mock(side_effect=_handler)
    return captured


# ---------------------------------------------------------------------------
# Flag off (default) -> no linkDetails in fields, no link_details in output
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_flag_off_no_field_param_and_no_output_key(server: FastMCP) -> None:
    """Default call (no include_* flags) sends no 'fields' param at all.

    Substitution probe: if include_link_details defaulted to True, or if the
    fields-building branch fired unconditionally, this would assert 'fields'
    present with 'linkDetails' in it -- this test fails in that case.
    """
    payload = {
        "result": [{"id": 1, "type": "task", "name": "n1", "status": "open"}],
        "total": 1,
        "continue": None,
    }

    with respx.mock(assert_all_called=True) as mock:
        captured = _mock_response(mock, payload)
        result = await _call(server, {})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured) == 1
    sent_params = captured[0].url.params
    assert "fields" not in sent_params, f"fields must be absent, got: {sent_params}"

    row = result["result"][0]
    assert "link_details" not in row, f"link_details must be absent, got: {row!r}"
    assert "linkDetails" not in row


@pytest.mark.asyncio
async def test_flag_off_byte_identical_even_if_backend_sends_linkdetails(
    server: FastMCP,
) -> None:
    """Flag off + backend somehow returns a raw linkDetails key anyway (defensive
    case) -> divoid_list must pass the row through untouched, not normalize it.

    This locks in "byte-identical back-compat": the per-row normalization loop
    must be gated strictly on include_link_details, not on key presence.
    """
    payload = {
        "result": [
            {
                "id": 1,
                "type": "task",
                "linkDetails": [{"sourceId": 10, "targetId": 20}],
            }
        ],
        "total": 1,
        "continue": None,
    }

    with respx.mock(assert_all_called=True) as mock:
        _mock_response(mock, payload)
        result = await _call(server, {})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    row = result["result"][0]
    assert row["linkDetails"] == [{"sourceId": 10, "targetId": 20}], (
        f"Untouched pass-through expected when flag is off, got: {row!r}"
    )
    assert "link_details" not in row


# ---------------------------------------------------------------------------
# Flag on -> linkDetails appended to fields, link_details normalized in output
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_flag_on_appends_field_and_normalizes_output(server: FastMCP) -> None:
    """include_link_details=True appends 'linkDetails' to fields and the
    result row carries normalized link_details, with the raw camelCase key
    removed.
    """
    payload = {
        "result": [
            {
                "id": 1,
                "type": "task",
                "linkDetails": [
                    {
                        "sourceId": 10,
                        "targetId": 20,
                        "linkType": "Unidirectional",
                        "context": "subtask",
                    }
                ],
            }
        ],
        "total": 1,
        "continue": None,
    }

    with respx.mock(assert_all_called=True) as mock:
        captured = _mock_response(mock, payload)
        result = await _call(server, {"include_link_details": True})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    sent_fields = captured[0].url.params.get_list("fields")
    assert "linkDetails" in sent_fields, f"Expected linkDetails in fields, got: {sent_fields}"

    row = result["result"][0]
    assert "linkDetails" not in row, "raw camelCase key must be replaced, not left in place"
    assert row["link_details"] == [
        {
            "source_id": 10,
            "target_id": 20,
            "link_type": "Unidirectional",
            "context": "subtask",
        }
    ], f"Unexpected link_details shape: {row.get('link_details')!r}"


@pytest.mark.asyncio
async def test_flag_on_missing_link_type_context_not_fabricated(server: FastMCP) -> None:
    """A linkDetails entry without linkType/context (pre-#163 backend row
    shape) normalizes to only source_id/target_id -- no fabricated nulls
    (same back-compat contract as divoid_get_links, DiVoid #7147).
    """
    payload = {
        "result": [
            {"id": 1, "linkDetails": [{"sourceId": 10, "targetId": 20}]},
        ],
        "total": 1,
        "continue": None,
    }

    with respx.mock(assert_all_called=True) as mock:
        _mock_response(mock, payload)
        result = await _call(server, {"include_link_details": True})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    row = result["result"][0]
    assert row["link_details"] == [{"source_id": 10, "target_id": 20}]
    assert "link_type" not in row["link_details"][0]
    assert "context" not in row["link_details"][0]


@pytest.mark.asyncio
async def test_flag_on_isolated_node_empty_list(server: FastMCP) -> None:
    """A node with no incident edges -> link_details is an empty list, not
    absent (mirrors 'links' semantics for isolated nodes).
    """
    payload = {
        "result": [{"id": 1, "linkDetails": []}],
        "total": 1,
        "continue": None,
    }

    with respx.mock(assert_all_called=True) as mock:
        _mock_response(mock, payload)
        result = await _call(server, {"include_link_details": True})

    assert result["result"][0]["link_details"] == []


# ---------------------------------------------------------------------------
# Composes with include_links: both flags together
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_composes_with_include_links(server: FastMCP) -> None:
    """include_links + include_link_details together append both 'links' and
    'linkDetails' to fields; the row carries both keys, 'links' untouched and
    'link_details' normalized.
    """
    payload = {
        "result": [
            {
                "id": 1,
                "links": [20],
                "linkDetails": [{"sourceId": 1, "targetId": 20, "linkType": "Bidirectional"}],
            }
        ],
        "total": 1,
        "continue": None,
    }

    with respx.mock(assert_all_called=True) as mock:
        captured = _mock_response(mock, payload)
        result = await _call(
            server, {"include_links": True, "include_link_details": True}
        )

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    sent_fields = captured[0].url.params.get_list("fields")
    assert "links" in sent_fields
    assert "linkDetails" in sent_fields

    row = result["result"][0]
    assert row["links"] == [20], "links must remain untouched passthrough"
    assert row["link_details"] == [
        {"source_id": 1, "target_id": 20, "link_type": "Bidirectional"}
    ]
