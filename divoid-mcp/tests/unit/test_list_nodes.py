"""
Unit tests for divoid_list's link_details enrichment.

Mirrors the include_links precedent and the divoid_get_links normalization
convention (see test_get_links.py): these tests mock the HTTP transport (via
respx) and assert on both the outbound `fields` query param and the exact
result rows divoid_list produces from a given backend JSON payload.

  - Default (flag off) -> 'linkDetails' and 'id' are appended to the fields
    projection unconditionally, even when the caller passed an explicit
    fields= that omits them; each row surfaces only the edges that carry a
    context (a human label such as supersedes) as 'link_details', omitted
    entirely when none do.
  - Flag on -> 'link_details' widens to every incident edge, normalized the
    same way (source_id/target_id always present; link_type/context
    pass-through, surfaced only when the backend row carries them).
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
    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY, source="env")
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
# Default (flag off) -> linkDetails/id still requested; row shows labelled edges
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_default_call_requests_link_details(server: FastMCP) -> None:
    """A default call (no include_* flags) still sends 'fields' with
    'linkDetails' and 'id' in it -- default-on enrichment needs no flag.

    Substitution probe: reverting the fields-building back to firing only
    when an include_* flag is set makes this assert fail.
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
    sent_fields = captured[0].url.params.get_list("fields")
    assert "linkDetails" in sent_fields, f"Expected linkDetails in fields, got: {sent_fields}"
    assert "id" in sent_fields, f"Expected id in fields, got: {sent_fields}"


@pytest.mark.asyncio
async def test_default_row_carries_labelled_edges(server: FastMCP) -> None:
    """A default (flag off) row surfaces the edges that carry a context.

    Substitution probe: a selector that always returns [] makes this fail.
    """
    payload = {
        "result": [
            {
                "id": 1,
                "linkDetails": [
                    {
                        "sourceId": 13,
                        "targetId": 1,
                        "linkType": "Unidirectional",
                        "context": "supersedes",
                    },
                ],
            }
        ],
        "total": 1,
        "continue": None,
    }

    with respx.mock(assert_all_called=True) as mock:
        _mock_response(mock, payload)
        result = await _call(server, {})

    row = result["result"][0]
    assert row["link_details"] == [
        {"source_id": 13, "target_id": 1, "link_type": "Unidirectional", "context": "supersedes"}
    ]
    assert "linkDetails" not in row, "raw camelCase key must be replaced, not left in place"


@pytest.mark.asyncio
async def test_default_row_omits_unlabelled_edges(server: FastMCP) -> None:
    """A default (flag off) row drops edges with no context, keeping only
    the labelled one.

    Substitution probe: a predicate that always returns True (no filtering)
    makes this fail -- the unlabelled edge would leak into the row.
    """
    payload = {
        "result": [
            {
                "id": 1,
                "linkDetails": [
                    {"sourceId": 13, "targetId": 1, "context": "supersedes"},
                    {"sourceId": 1, "targetId": 99},
                ],
            }
        ],
        "total": 1,
        "continue": None,
    }

    with respx.mock(assert_all_called=True) as mock:
        _mock_response(mock, payload)
        result = await _call(server, {})

    row = result["result"][0]
    assert row["link_details"] == [{"source_id": 13, "target_id": 1, "context": "supersedes"}]


@pytest.mark.asyncio
async def test_row_without_labelled_edges_omits_key(server: FastMCP) -> None:
    """A default (flag off) row whose edges all lack a context omits
    'link_details' entirely rather than emitting an empty list.

    Substitution probe: emitting [] unconditionally makes this fail.
    """
    payload = {
        "result": [{"id": 1, "linkDetails": [{"sourceId": 1, "targetId": 99}]}],
        "total": 1,
        "continue": None,
    }

    with respx.mock(assert_all_called=True) as mock:
        _mock_response(mock, payload)
        result = await _call(server, {})

    row = result["result"][0]
    assert "link_details" not in row, f"link_details must be absent, got: {row!r}"


@pytest.mark.asyncio
async def test_list_forces_id_when_appending_link_details(server: FastMCP) -> None:
    """A caller-supplied fields= that omits 'id' still gets 'id' forced in,
    so linkDetails' backend adjacency lookup (keyed on id) never silently
    resolves to zero edges.

    Substitution probe: this must assert on the outgoing 'fields' param, not
    the response -- a mocked response returns edges regardless of whether
    'id' was actually sent, so an assertion against the response body would
    pass even with the force-include removed.
    """
    payload = {"result": [{"name": "n1"}], "total": 1, "continue": None}

    with respx.mock(assert_all_called=True) as mock:
        captured = _mock_response(mock, payload)
        result = await _call(server, {"fields": ["name"]})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    sent_fields = captured[0].url.params.get_list("fields")
    assert "id" in sent_fields, f"Expected id force-included, got: {sent_fields}"
    assert "linkDetails" in sent_fields, f"Expected linkDetails in fields, got: {sent_fields}"


@pytest.mark.asyncio
async def test_default_fields_keep_severity_and_root_node_id(server: FastMCP) -> None:
    """A default (no fields=, no include_* flags) call still requests
    severity and rootNodeId, so they don't silently drop out of every
    caller's row now that fields is sent unconditionally.

    Substitution probe: dropping either name from _DEFAULT_FIELDS makes the
    call still succeed but the key vanishes from both the request and,
    because divoid_list is pass-through, the row.
    """
    payload = {"result": [{"id": 1, "severity": 3, "rootNodeId": 3}], "total": 1, "continue": None}

    with respx.mock(assert_all_called=True) as mock:
        captured = _mock_response(mock, payload)
        result = await _call(server, {})

    sent_fields = captured[0].url.params.get_list("fields")
    assert "severity" in sent_fields, f"Expected severity in fields, got: {sent_fields}"
    assert "rootNodeId" in sent_fields, f"Expected rootNodeId in fields, got: {sent_fields}"

    row = result["result"][0]
    assert row["severity"] == 3
    assert row["rootNodeId"] == 3


@pytest.mark.asyncio
async def test_default_fields_keep_owner_id_value_not_fabricated_zero(server: FastMCP) -> None:
    """A default call still requests ownerId. The outgoing fields param is
    what this test discriminates on: under a mocked backend the row simply
    echoes back whatever the payload says, so a mutation that drops
    ownerId from _DEFAULT_FIELDS changes only the request, not the mocked
    response -- the value assertion below cannot see that mutation and is
    not the guard here.

    That value assertion still earns its place, as a live-verification
    claim rather than a discriminator: it records the real shape this
    guard protects -- a non-nullable backend column that fabricates 0
    instead of omitting itself when unrequested (same CLR-default class as
    severity/rootNodeId, but unlike them a plain key-presence check on the
    row can never catch it, since the backend emits the key either way).
    Confirmed against production separately; not reproducible against a
    mock.

    Substitution probe: dropping ownerId from _DEFAULT_FIELDS leaves
    'ownerId' absent from the outgoing fields param, which is what the
    assertion on sent_fields below catches.
    """
    payload = {"result": [{"id": 1, "ownerId": 2}], "total": 1, "continue": None}

    with respx.mock(assert_all_called=True) as mock:
        captured = _mock_response(mock, payload)
        result = await _call(server, {})

    sent_fields = captured[0].url.params.get_list("fields")
    assert "ownerId" in sent_fields, f"Expected ownerId in fields, got: {sent_fields}"

    row = result["result"][0]
    assert row["ownerId"] == 2, f"Expected the real owner id, got: {row.get('ownerId')!r}"


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
    """A linkDetails entry without linkType/context normalizes to only
    source_id/target_id -- no fabricated nulls (same contract as
    divoid_get_links).
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


@pytest.mark.asyncio
async def test_flag_on_returns_unlabelled_edges_too(server: FastMCP) -> None:
    """include_link_details=True widens the row to every incident edge, not
    just the labelled subset -- a labelled and an unlabelled edge in the
    same payload both survive unchanged.

    Substitution probe: applying the labelled-only selector on the flag-on
    path too would drop the unlabelled entry. A fixture carrying only a
    labelled edge cannot discriminate that mutation, since dropping the
    unlabelled one leaves nothing visibly missing; this fixture carries one
    of each so the drop is observable.
    """
    payload = {
        "result": [
            {
                "id": 1,
                "linkDetails": [
                    {"sourceId": 13, "targetId": 1, "context": "supersedes"},
                    {"sourceId": 1, "targetId": 99},
                ],
            }
        ],
        "total": 1,
        "continue": None,
    }

    with respx.mock(assert_all_called=True) as mock:
        _mock_response(mock, payload)
        result = await _call(server, {"include_link_details": True})

    row = result["result"][0]
    assert row["link_details"] == [
        {"source_id": 13, "target_id": 1, "context": "supersedes"},
        {"source_id": 1, "target_id": 99},
    ]


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
