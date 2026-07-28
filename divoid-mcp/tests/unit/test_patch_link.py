"""
Unit tests for divoid_patch_link: edit an existing edge's linkType/context.

These tests mock the HTTP transport layer (via respx) and assert on the exact
outbound PATCH request the tool builds, plus the normalized return shape. They
pin the DiVoid #7206 (MCP tool) / #7201+PR #170 (backend) contract:

  - link_type only -> body is [{"op":"replace","path":"/linkType","value":...}]
  - context only -> body is [{"op":"replace","path":"/context","value":...}]
  - clear_context -> body is [{"op":"replace","path":"/context","value":None}]
  - link_type + context together -> both ops present, in one PATCH
  - no fields at all -> no_fields_to_patch invariant fires, no HTTP call
  - source_id == target_id -> same_node_link guard fires, no HTTP call
  - the returned NodeLink is normalized to snake_case
    {source_id, target_id, link_type, context}, mirroring divoid_get_links

No network calls and no DiVoid credentials are required.

Architecture reference: DiVoid #7206 (this tool). Backend reference: DiVoid
#7201 / PR #170 (PATCH /api/nodes/{source}/links/{target}).
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
from divoid_mcp.tools.patch_link import register as register_patch_link

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

_DUMMY_BASE = "http://divoid.test"
_DUMMY_KEY = "dummy-key-for-unit-tests"

_SOURCE_ID = 10
_TARGET_ID = 20
_LINK_URL = f"{_DUMMY_BASE}/nodes/{_SOURCE_ID}/links/{_TARGET_ID}"


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture(scope="module")
def server() -> FastMCP:
    """
    Module-scoped FastMCP server with only divoid_patch_link registered.

    Uses dummy config -- no real credentials, no network. http_client is
    initialised with the dummy base URL so respx can intercept every call.
    """
    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)
    http_client.init(_DUMMY_BASE, _DUMMY_KEY)

    mcp_server = FastMCP("divoid-mcp-patch-link-test")
    mcp_server.config = config  # type: ignore[attr-defined]
    register_patch_link(mcp_server)

    return mcp_server


async def _call(server: FastMCP, args: dict[str, Any]) -> dict[str, Any]:
    """Call divoid_patch_link with the given args and return the raw dict."""
    result = await server._tool_manager.call_tool("divoid_patch_link", args)
    assert isinstance(result, dict), f"Expected dict, got {type(result)}"
    return result


def _mock_ok(mock: respx.MockRouter, captured: list[httpx.Request], payload: dict[str, Any]) -> None:
    def capture(req: httpx.Request) -> httpx.Response:
        captured.append(req)
        return httpx.Response(200, json=payload)

    mock.patch(_LINK_URL).mock(side_effect=capture)


# ---------------------------------------------------------------------------
# link_type only -> single /linkType replace op
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_link_type_only_emits_single_link_type_op(server: FastMCP) -> None:
    """link_type supplied alone -> PATCH body has exactly one /linkType op.

    Substitution probe: composing context into the ops list unconditionally
    (instead of only when not None) would add a spurious /context op here.
    """
    captured: list[httpx.Request] = []
    payload = {"sourceId": _SOURCE_ID, "targetId": _TARGET_ID, "linkType": "Unidirectional", "context": None}

    with respx.mock(assert_all_called=True) as mock:
        _mock_ok(mock, captured, payload)
        result = await _call(server, {
            "source_id": _SOURCE_ID,
            "target_id": _TARGET_ID,
            "link_type": "Unidirectional",
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured) == 1, f"Expected exactly 1 PATCH, got {len(captured)}"
    ops = json.loads(captured[0].content)
    assert ops == [{"op": "replace", "path": "/linkType", "value": "Unidirectional"}], (
        f"Expected a single /linkType replace op, got: {ops!r}"
    )


# ---------------------------------------------------------------------------
# context only -> single /context replace op
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_context_only_emits_single_context_op(server: FastMCP) -> None:
    """context supplied alone -> PATCH body has exactly one /context op."""
    captured: list[httpx.Request] = []
    payload = {"sourceId": _SOURCE_ID, "targetId": _TARGET_ID, "linkType": "None", "context": "subtask"}

    with respx.mock(assert_all_called=True) as mock:
        _mock_ok(mock, captured, payload)
        result = await _call(server, {
            "source_id": _SOURCE_ID,
            "target_id": _TARGET_ID,
            "context": "subtask",
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    ops = json.loads(captured[0].content)
    assert ops == [{"op": "replace", "path": "/context", "value": "subtask"}], (
        f"Expected a single /context replace op, got: {ops!r}"
    )


# ---------------------------------------------------------------------------
# clear_context -> /context replace null
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_clear_context_emits_context_null_op(server: FastMCP) -> None:
    """clear_context=True -> PATCH body has a /context replace op with value null.

    Substitution probe: checking `context is not None` unconditionally without
    the elif-clear_context branch would silently drop this op entirely.
    """
    captured: list[httpx.Request] = []
    payload = {"sourceId": _SOURCE_ID, "targetId": _TARGET_ID, "linkType": "None", "context": None}

    with respx.mock(assert_all_called=True) as mock:
        _mock_ok(mock, captured, payload)
        result = await _call(server, {
            "source_id": _SOURCE_ID,
            "target_id": _TARGET_ID,
            "clear_context": True,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    ops = json.loads(captured[0].content)
    assert ops == [{"op": "replace", "path": "/context", "value": None}], (
        f"Expected a single /context=null replace op, got: {ops!r}"
    )


@pytest.mark.asyncio
async def test_explicit_context_wins_over_clear_context(server: FastMCP) -> None:
    """context and clear_context both supplied -> the explicit context value wins.

    Mirrors divoid_patch_node's severity/root_node_id "explicit value wins"
    convention for its clear_* pairs.
    """
    captured: list[httpx.Request] = []
    payload = {"sourceId": _SOURCE_ID, "targetId": _TARGET_ID, "linkType": "None", "context": "kept"}

    with respx.mock(assert_all_called=True) as mock:
        _mock_ok(mock, captured, payload)
        result = await _call(server, {
            "source_id": _SOURCE_ID,
            "target_id": _TARGET_ID,
            "context": "kept",
            "clear_context": True,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    ops = json.loads(captured[0].content)
    assert ops == [{"op": "replace", "path": "/context", "value": "kept"}], (
        f"Expected the explicit context value to win over clear_context, got: {ops!r}"
    )


# ---------------------------------------------------------------------------
# link_type + context together -> both ops present, in one PATCH
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_both_fields_emit_both_ops_in_one_patch(server: FastMCP) -> None:
    """link_type + context both supplied -> a single PATCH carries both ops."""
    captured: list[httpx.Request] = []
    payload = {"sourceId": _SOURCE_ID, "targetId": _TARGET_ID, "linkType": "Bidirectional", "context": "references"}

    with respx.mock(assert_all_called=True) as mock:
        _mock_ok(mock, captured, payload)
        result = await _call(server, {
            "source_id": _SOURCE_ID,
            "target_id": _TARGET_ID,
            "link_type": "Bidirectional",
            "context": "references",
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured) == 1, "Expected exactly one PATCH for a single tool call"
    ops = json.loads(captured[0].content)
    assert ops == [
        {"op": "replace", "path": "/linkType", "value": "Bidirectional"},
        {"op": "replace", "path": "/context", "value": "references"},
    ], f"Expected both ops present, got: {ops!r}"


# ---------------------------------------------------------------------------
# Returned NodeLink is normalized to snake_case, mirroring divoid_get_links
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_returned_link_normalized_to_snake_case(server: FastMCP) -> None:
    """The backend's camelCase NodeLink response is normalized to snake_case.

    Substitution probe: returning the raw backend dict unchanged (still
    sourceId/targetId/linkType) would fail this assertion.
    """
    captured: list[httpx.Request] = []
    payload = {"sourceId": _SOURCE_ID, "targetId": _TARGET_ID, "linkType": "Bidirectional", "context": "references"}

    with respx.mock(assert_all_called=True) as mock:
        _mock_ok(mock, captured, payload)
        result = await _call(server, {
            "source_id": _SOURCE_ID,
            "target_id": _TARGET_ID,
            "link_type": "Bidirectional",
            "context": "references",
        })

    assert result == {
        "source_id": _SOURCE_ID,
        "target_id": _TARGET_ID,
        "link_type": "Bidirectional",
        "context": "references",
    }, f"Unexpected normalized shape: {result!r}"


# ---------------------------------------------------------------------------
# no fields at all -> no_fields_to_patch invariant, no HTTP call
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_no_fields_rejected_before_http(server: FastMCP) -> None:
    """No link_type, context, or clear_context -> isError, no HTTP call.

    Substitution probe: removing the invariant guard would let this call
    reach http_client.patch_json with an empty ops array -- a no-op PATCH
    that silently succeeds instead of telling the caller nothing was done.
    """
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json={})

        mock.patch(_LINK_URL).mock(side_effect=detect)

        result = await _call(server, {"source_id": _SOURCE_ID, "target_id": _TARGET_ID})

    assert result.get("isError") is True, f"Expected isError=True for no fields, got: {result}"
    assert not http_called, "HTTP must NOT be called when no fields are supplied"
    text = result["content"][0]["text"]
    assert "no_fields_to_patch" in text, f"Expected 'no_fields_to_patch' code, got: {text!r}"


# ---------------------------------------------------------------------------
# source_id == target_id -> same_node_link guard, no HTTP call
# (regression guard -- mirrors divoid_link_nodes/divoid_unlink_nodes)
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_same_node_rejected_before_http(server: FastMCP) -> None:
    """source_id == target_id -> isError, no HTTP call, even with fields set."""
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json={})

        mock.patch(f"{_DUMMY_BASE}/nodes/{_SOURCE_ID}/links/{_SOURCE_ID}").mock(side_effect=detect)

        result = await _call(server, {
            "source_id": _SOURCE_ID,
            "target_id": _SOURCE_ID,
            "link_type": "Unidirectional",
        })

    assert result.get("isError") is True, f"Expected isError=True for self-link, got: {result}"
    assert not http_called, "HTTP must NOT be called when source_id == target_id"
    text = result["content"][0]["text"]
    assert "same_node_link" in text, f"Expected 'same_node_link' code, got: {text!r}"


# ---------------------------------------------------------------------------
# source_id / target_id must be positive integers
# (regression guard -- mirrors divoid_link_nodes/divoid_unlink_nodes)
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_non_positive_source_id_rejected_before_http(server: FastMCP) -> None:
    """source_id < 1 -> isError, no HTTP call."""
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json={})

        mock.patch(url__regex=r".*").mock(side_effect=detect)

        result = await _call(server, {
            "source_id": 0,
            "target_id": _TARGET_ID,
            "link_type": "Unidirectional",
        })

    assert result.get("isError") is True, f"Expected isError=True for source_id=0, got: {result}"
    assert not http_called, "HTTP must NOT be called when source_id < 1"
    text = result["content"][0]["text"]
    assert "divoid_bad_request" in text, f"Expected 'divoid_bad_request' code, got: {text!r}"
