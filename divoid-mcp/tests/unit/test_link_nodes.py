"""
Unit tests for divoid_link_nodes: optional link_type + context query-param plumbing.

These tests mock the HTTP transport layer (via respx) and assert on the exact
outbound request the tool builds. They pin the DiVoid #7138 write contract:

  - No params supplied -> request is byte-identical to before link_type/context
    existed (no query string at all).
  - link_type supplied -> "?linkType=<value>" on the POST.
  - context supplied -> "?context=<value>" on the POST.
  - Both supplied -> both query params present.
  - Re-linking (calling twice) issues a POST each time -- the tool does not
    special-case or skip the second call; the backend owns re-link idempotency.

The request BODY must stay the bare target-id integer throughout -- these tests
also pin that the body never becomes an object when the new params are used.

No network calls and no DiVoid credentials are required.

Architecture reference: DiVoid #7119 (backend), #7120 (design), #7138 (this tool).
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
from divoid_mcp.tools.link_nodes import register as register_link_nodes

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

_DUMMY_BASE = "http://divoid.test"
_DUMMY_KEY = "dummy-key-for-unit-tests"

_SOURCE_ID = 10
_TARGET_ID = 20
_LINKS_URL = f"{_DUMMY_BASE}/nodes/{_SOURCE_ID}/links"


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture(scope="module")
def server() -> FastMCP:
    """
    Module-scoped FastMCP server with only divoid_link_nodes registered.

    Uses dummy config -- no real credentials, no network. http_client is
    initialised with the dummy base URL so respx can intercept every call.
    """
    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)
    http_client.init(_DUMMY_BASE, _DUMMY_KEY)

    mcp_server = FastMCP("divoid-mcp-link-nodes-test")
    mcp_server.config = config  # type: ignore[attr-defined]
    register_link_nodes(mcp_server)

    return mcp_server


async def _call(server: FastMCP, args: dict[str, Any]) -> dict[str, Any]:
    """Call divoid_link_nodes with the given args and return the raw dict."""
    result = await server._tool_manager.call_tool("divoid_link_nodes", args)
    assert isinstance(result, dict), f"Expected dict, got {type(result)}"
    return result


def _mock_ok(mock: respx.MockRouter, captured: list[httpx.Request]) -> None:
    def capture(req: httpx.Request) -> httpx.Response:
        captured.append(req)
        return httpx.Response(200, json={})

    mock.post(_LINKS_URL).mock(side_effect=capture)


# ---------------------------------------------------------------------------
# Back-compat: neither param supplied -> no query string at all
#
# Substitution probe: always attach params={} (instead of None) in link_nodes.py
# -- httpx still renders an empty query string as no "?" in most cases, so the
# stronger probe is passing params={"linkType": None, "context": None} instead
# of omitting unset keys entirely; that would put literal "None" in the URL.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_default_call_emits_no_query_string(server: FastMCP) -> None:
    """No link_type/context -> POST has no query string (strict back-compat)."""
    captured: list[httpx.Request] = []

    with respx.mock(assert_all_called=True) as mock:
        _mock_ok(mock, captured)
        result = await _call(server, {"source_id": _SOURCE_ID, "target_id": _TARGET_ID})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured) == 1, f"Expected exactly 1 POST, got {len(captured)}"
    req = captured[0]
    assert req.url.query == b"", (
        f"Expected no query string on the default call, got: {req.url.query!r}. "
        "Substitution probe: passing an empty dict (not None) through to httpx's "
        "params kwarg would still be fine here, but passing unset keys as None "
        "would leak 'linkType=None'/'context=None' into the URL -- this assertion "
        "catches that regression."
    )
    body = json.loads(req.content)
    assert body == _TARGET_ID, (
        f"Expected bare target-id body ({_TARGET_ID}), got: {body!r}. "
        "The write contract (#7120) requires the body stay a bare long, never an object."
    )


# ---------------------------------------------------------------------------
# link_type supplied -> ?linkType=<value>
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_link_type_emits_link_type_query_param(server: FastMCP) -> None:
    """link_type='Unidirectional' -> POST has ?linkType=Unidirectional.

    Substitution probe: rename the query key to "link_type" (snake_case) instead
    of "linkType" -- the backend's [FromQuery] parameter is camelCase, so this
    would 400 in real use; this test catches the wrong key name before that.
    """
    captured: list[httpx.Request] = []

    with respx.mock(assert_all_called=True) as mock:
        _mock_ok(mock, captured)
        result = await _call(server, {
            "source_id": _SOURCE_ID,
            "target_id": _TARGET_ID,
            "link_type": "Unidirectional",
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    req = captured[0]
    assert req.url.params.get("linkType") == "Unidirectional", (
        f"Expected linkType=Unidirectional in query, got params: {dict(req.url.params)!r}"
    )
    assert "context" not in req.url.params, (
        f"context must be absent when not supplied, got params: {dict(req.url.params)!r}"
    )
    body = json.loads(req.content)
    assert body == _TARGET_ID, f"Body must stay a bare target id, got: {body!r}"


# ---------------------------------------------------------------------------
# context supplied -> ?context=<value>
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_context_emits_context_query_param(server: FastMCP) -> None:
    """context='subtask' -> POST has ?context=subtask, no linkType."""
    captured: list[httpx.Request] = []

    with respx.mock(assert_all_called=True) as mock:
        _mock_ok(mock, captured)
        result = await _call(server, {
            "source_id": _SOURCE_ID,
            "target_id": _TARGET_ID,
            "context": "subtask",
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    req = captured[0]
    assert req.url.params.get("context") == "subtask", (
        f"Expected context=subtask in query, got params: {dict(req.url.params)!r}"
    )
    assert "linkType" not in req.url.params, (
        f"linkType must be absent when not supplied, got params: {dict(req.url.params)!r}"
    )


# ---------------------------------------------------------------------------
# Both supplied -> both query params present, in one request
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_both_params_emit_both_query_params(server: FastMCP) -> None:
    """link_type + context both supplied -> both appear on the same POST."""
    captured: list[httpx.Request] = []

    with respx.mock(assert_all_called=True) as mock:
        _mock_ok(mock, captured)
        result = await _call(server, {
            "source_id": _SOURCE_ID,
            "target_id": _TARGET_ID,
            "link_type": "Bidirectional",
            "context": "references",
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured) == 1, "Expected exactly one POST for a single tool call"
    req = captured[0]
    assert req.url.params.get("linkType") == "Bidirectional", (
        f"Expected linkType=Bidirectional, got params: {dict(req.url.params)!r}"
    )
    assert req.url.params.get("context") == "references", (
        f"Expected context=references, got params: {dict(req.url.params)!r}"
    )


# ---------------------------------------------------------------------------
# Re-link (calling twice) is unaffected by the new params -- the tool issues
# a POST every time and relays whatever the backend does; it does not try to
# detect "this pair is already linked" client-side (that stays the backend's
# idempotent-no-op contract, bug #702 / design #7120 D5).
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_relink_with_params_still_posts_every_call(server: FastMCP) -> None:
    """Calling divoid_link_nodes twice on the same pair issues two POSTs.

    Substitution probe: add a client-side "already linked, skip" short-circuit
    to link_nodes.py -- the second call would not reach http_client.post_json
    and captured would have length 1 instead of 2.
    """
    captured: list[httpx.Request] = []

    with respx.mock(assert_all_called=True) as mock:
        _mock_ok(mock, captured)

        first = await _call(server, {"source_id": _SOURCE_ID, "target_id": _TARGET_ID})
        second = await _call(server, {
            "source_id": _SOURCE_ID,
            "target_id": _TARGET_ID,
            "link_type": "Bidirectional",
            "context": "shouldNotApply",
        })

    assert first.get("isError") is not True, f"Expected success, got: {first}"
    assert second.get("isError") is not True, f"Expected success, got: {second}"
    assert len(captured) == 2, (
        f"Expected 2 POSTs (one per call, no client-side dedup), got {len(captured)}. "
        "Substitution probe: a client-side idempotency short-circuit would leave "
        "the second call from ever reaching http_client.post_json."
    )
    # First call: no params.
    assert captured[0].url.query == b"", f"First call must have no query string, got: {captured[0].url.query!r}"
    # Second call: params present -- the tool always forwards them; whether the
    # backend actually applies them on an existing pair is a backend contract
    # (#7120 D5), not something this tool inspects or short-circuits.
    assert captured[1].url.params.get("linkType") == "Bidirectional"
    assert captured[1].url.params.get("context") == "shouldNotApply"


# ---------------------------------------------------------------------------
# Existing structural guards must still fire before any HTTP call
# (regression guard -- these predate #7138 but must survive the signature change)
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_same_node_link_rejected_before_http(server: FastMCP) -> None:
    """source_id == target_id -> isError, no HTTP call, even with params set."""
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json={})

        mock.post(_LINKS_URL).mock(side_effect=detect)

        result = await _call(server, {
            "source_id": _SOURCE_ID,
            "target_id": _SOURCE_ID,
            "link_type": "Unidirectional",
        })

    assert result.get("isError") is True, f"Expected isError=True for self-link, got: {result}"
    assert not http_called, "HTTP must NOT be called when source_id == target_id"
    text = result["content"][0]["text"]
    assert "same_node_link" in text, f"Expected 'same_node_link' code, got: {text!r}"
