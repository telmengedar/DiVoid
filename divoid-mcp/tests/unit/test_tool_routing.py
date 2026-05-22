"""
Hermetic tool-level routing tests for divoid_get_content and divoid_search.

These tests mock the HTTP transport layer (via respx) and assert on the
MCP-side response shape. They exercise each routing branch in isolation,
with no network calls and no DiVoid credentials required.

Why this tier exists: the smoke tests in tests/smoke/ pin the DiVoid API
contract by calling the real server. They do NOT exercise the tool's
internal branch routing logic. A regression in get_content.py that collapses
the two 404 branches would pass the smoke suite undetected. These tests catch
exactly that class of regression.

Architecture reference: DiVoid task #705, architecture doc #695.
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
from divoid_mcp.tools.get_content import register as register_get_content
from divoid_mcp.tools.search import register as register_search

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

# Dummy base URL — valid URL form so httpx builds the request, but respx
# intercepts every outbound call so it never hits the network.
_DUMMY_BASE = "http://divoid.test"
_DUMMY_KEY = "dummy-key-for-unit-tests"

# URL templates matching what http_client.get() constructs at runtime.
_CONTENT_URL = f"{_DUMMY_BASE}/nodes/{{id}}/content"
_NODES_URL = f"{_DUMMY_BASE}/nodes"


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture(scope="module")
def server() -> FastMCP:
    """
    Module-scoped server with only the two tools under test registered.

    Uses a dummy config — no real credentials, no network. The http_client
    module-level client is initialised with the dummy base URL so respx can
    intercept it.
    """
    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)
    http_client.init(_DUMMY_BASE, _DUMMY_KEY)

    mcp_server = FastMCP("divoid-mcp-test")
    mcp_server.config = config  # type: ignore[attr-defined]

    register_get_content(mcp_server)
    register_search(mcp_server)

    return mcp_server


async def _call(server: FastMCP, tool: str, args: dict[str, Any]) -> dict[str, Any]:
    """Call a named tool and return the raw dict result."""
    result = await server._tool_manager.call_tool(tool, args)
    assert isinstance(result, dict), f"Expected dict from {tool}, got {type(result)}"
    return result


# ---------------------------------------------------------------------------
# divoid_get_content — branch 1: missing node
#
# API returns 404; body is a JSON object whose 'text' field does NOT contain
# "has no content". The tool must return an MCP error with code node_not_found.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_get_content_missing_node(server: FastMCP) -> None:
    """404 without 'has no content' in body → node_not_found MCP error."""
    node_id = 999_999_999
    body = json.dumps({
        "code": "data_entitynotfound",
        "text": f"'Node' with id '{node_id}' not found",
    }).encode()

    with respx.mock(assert_all_called=False) as mock:
        mock.get(_CONTENT_URL.format(id=node_id)).mock(
            return_value=httpx.Response(404, content=body)
        )
        result = await _call(server, "divoid_get_content", {"id": node_id})

    assert result.get("isError") is True, "Expected isError=True for missing node"
    content_text: str = result["content"][0]["text"]
    assert "node_not_found" in content_text, (
        f"Expected 'node_not_found' in error text, got: {content_text!r}"
    )


# ---------------------------------------------------------------------------
# divoid_get_content — branch 2: existing node with no content
#
# API returns 404; body JSON 'text' field CONTAINS "has no content".
# The tool must return a success shape: {content: "", content_type: null,
# byte_length: 0} — NOT an MCP error.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_get_content_no_content_node(server: FastMCP) -> None:
    """404 with 'has no content' in body → success with empty content shape."""
    node_id = 42
    body = json.dumps({
        "code": "data_entitynotfound",
        "text": f"Node {node_id} has no content",
    }).encode()

    with respx.mock(assert_all_called=False) as mock:
        mock.get(_CONTENT_URL.format(id=node_id)).mock(
            return_value=httpx.Response(404, content=body)
        )
        result = await _call(server, "divoid_get_content", {"id": node_id})

    assert result.get("isError") is not True, (
        f"Expected success (not error) for node-with-no-content, got: {result}"
    )
    assert result.get("content") == "", (
        f"Expected content='', got: {result.get('content')!r}"
    )
    assert result.get("content_type") is None, (
        f"Expected content_type=None, got: {result.get('content_type')!r}"
    )
    assert result.get("byte_length") == 0, (
        f"Expected byte_length=0, got: {result.get('byte_length')!r}"
    )


# ---------------------------------------------------------------------------
# divoid_get_content — branch 3: existing node with content
#
# API returns 200 with body bytes and a Content-Type header.
# The tool must return a success shape with decoded content, content_type,
# and byte_length.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_get_content_with_content(server: FastMCP) -> None:
    """200 with body and Content-Type → success with decoded content fields."""
    node_id = 9
    body_text = "# Hello DiVoid\n\nThis is a test node."
    body_bytes = body_text.encode("utf-8")
    content_type = "text/markdown; charset=utf-8"

    with respx.mock(assert_all_called=False) as mock:
        mock.get(_CONTENT_URL.format(id=node_id)).mock(
            return_value=httpx.Response(
                200,
                content=body_bytes,
                headers={"content-type": content_type},
            )
        )
        result = await _call(server, "divoid_get_content", {"id": node_id})

    assert result.get("isError") is not True, (
        f"Expected success for node with content, got error: {result}"
    )
    assert result.get("content") == body_text, (
        f"Expected decoded content, got: {result.get('content')!r}"
    )
    assert result.get("content_type") == content_type, (
        f"Expected content_type={content_type!r}, got: {result.get('content_type')!r}"
    )
    assert result.get("byte_length") == len(body_bytes), (
        f"Expected byte_length={len(body_bytes)}, got: {result.get('byte_length')!r}"
    )


# ---------------------------------------------------------------------------
# divoid_search — case 1: typed node hit
#
# API returns a node with both 'type' and 'status' populated.
# The tool must include both fields in the result item.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_search_typed_node_hit(server: FastMCP) -> None:
    """Typed node in API response → result item has type and status."""
    api_response = {
        "result": [
            {
                "id": 123,
                "name": "Fix the auth bug",
                "type": "task",
                "status": "open",
                "similarity": 0.91,
            }
        ],
        "total": 1,
    }

    with respx.mock(assert_all_called=False) as mock:
        mock.get(_NODES_URL).mock(
            return_value=httpx.Response(200, json=api_response)
        )
        result = await _call(server, "divoid_search", {"query": "auth bug"})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    items = result.get("results", [])
    assert len(items) == 1, f"Expected 1 result, got {len(items)}"
    item = items[0]
    assert item.get("type") == "task", f"Expected type='task', got: {item.get('type')!r}"
    assert item.get("status") == "open", f"Expected status='open', got: {item.get('status')!r}"
    assert result.get("total") == 1, f"Expected total=1, got: {result.get('total')!r}"


# ---------------------------------------------------------------------------
# divoid_search — case 2: structural group hit (type=null, status=null)
#
# Group nodes (e.g. Tasks, Docs containers) carry type=null and status=null
# in the API response. The tool must not raise on normalisation and must
# propagate nulls faithfully.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_search_structural_group_hit(server: FastMCP) -> None:
    """Node with type=null and status=null → result item carries nulls, no exception."""
    api_response = {
        "result": [
            {
                "id": 314,
                "name": "Tasks",
                "type": None,
                "status": None,
                "similarity": 0.72,
            }
        ],
        "total": 1,
    }

    with respx.mock(assert_all_called=False) as mock:
        mock.get(_NODES_URL).mock(
            return_value=httpx.Response(200, json=api_response)
        )
        result = await _call(server, "divoid_search", {"query": "tasks group"})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    items = result.get("results", [])
    assert len(items) == 1, f"Expected 1 result, got {len(items)}"
    item = items[0]
    assert item.get("type") is None, (
        f"Expected type=None for structural group, got: {item.get('type')!r}"
    )
    assert item.get("status") is None, (
        f"Expected status=None for structural group, got: {item.get('status')!r}"
    )


# ---------------------------------------------------------------------------
# divoid_search — case 3: no results
#
# API returns an empty result array and total=0.
# The tool must return results=[] and total=0 without error.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_search_no_results(server: FastMCP) -> None:
    """Empty result array from API → results=[], total=0, no error."""
    api_response = {
        "result": [],
        "total": 0,
    }

    with respx.mock(assert_all_called=False) as mock:
        mock.get(_NODES_URL).mock(
            return_value=httpx.Response(200, json=api_response)
        )
        result = await _call(server, "divoid_search", {"query": "xyzzy nothing here"})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert result.get("results") == [], (
        f"Expected results=[], got: {result.get('results')!r}"
    )
    assert result.get("total") == 0, (
        f"Expected total=0, got: {result.get('total')!r}"
    )
