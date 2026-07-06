"""
Hermetic tool-level routing tests for divoid_get_content, divoid_search,
divoid_list, divoid_patch_node, divoid_get_node, and access+timestamp features.

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
from divoid_mcp.tools.delete_message import register as register_delete_message
from divoid_mcp.tools.get_content import register as register_get_content
from divoid_mcp.tools.get_node import register as register_get_node
from divoid_mcp.tools.list_nodes import register as register_list_nodes
from divoid_mcp.tools.patch_node import register as register_patch_node
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
_NODE_URL = f"{_DUMMY_BASE}/nodes/{{id}}"
_MESSAGES_URL = f"{_DUMMY_BASE}/messages/{{id}}"


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
    register_get_node(mcp_server)
    register_list_nodes(mcp_server)
    register_patch_node(mcp_server)
    register_search(mcp_server)
    register_delete_message(mcp_server)

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


# ---------------------------------------------------------------------------
# divoid_list — include_content tests (DiVoid #1181)
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_list_default_no_content_in_fields(server: FastMCP) -> None:
    """Default divoid_list call must NOT send fields=content and must NOT return content."""
    api_response = {
        "result": [{"id": 1, "type": "documentation", "name": "Onboarding", "status": None}],
        "total": 1,
    }

    captured_request: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_request.append(request)
            return httpx.Response(200, json=api_response)

        mock.get(_NODES_URL).mock(side_effect=capture)
        result = await _call(server, "divoid_list", {})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_request) == 1
    url_params = str(captured_request[0].url)
    assert "content" not in url_params, (
        f"Default call must not include 'content' in fields, URL: {url_params!r}"
    )
    rows = result.get("result", [])
    assert len(rows) == 1
    assert "content" not in rows[0], (
        f"Default call must not return 'content' in rows, got: {rows[0]!r}"
    )


@pytest.mark.asyncio
async def test_list_include_content_appends_to_default_fields(server: FastMCP) -> None:
    """include_content=True with no explicit fields → fields contains content + defaults."""
    api_response = {
        "result": [
            {
                "id": 9,
                "type": "documentation",
                "name": "Onboarding",
                "status": None,
                "contentType": "text/markdown",
                "content": "# Hello DiVoid",
            }
        ],
        "total": 1,
    }

    captured_request: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_request.append(request)
            return httpx.Response(200, json=api_response)

        mock.get(_NODES_URL).mock(side_effect=capture)
        result = await _call(server, "divoid_list", {"include_content": True})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_request) == 1
    url_params = str(captured_request[0].url)
    assert "content" in url_params, (
        f"include_content=True must add 'content' to fields, URL: {url_params!r}"
    )
    # The default projection fields must also be present.
    for expected_field in ("id", "type", "name", "status", "contentType"):
        assert expected_field in url_params, (
            f"Expected default field {expected_field!r} in URL, got: {url_params!r}"
        )
    rows = result.get("result", [])
    assert len(rows) == 1
    assert rows[0].get("content") == "# Hello DiVoid", (
        f"Expected content to be passed through, got: {rows[0].get('content')!r}"
    )
    assert rows[0].get("contentType") == "text/markdown", (
        f"Expected contentType to be passed through, got: {rows[0].get('contentType')!r}"
    )


@pytest.mark.asyncio
async def test_list_include_content_appends_to_explicit_fields(server: FastMCP) -> None:
    """include_content=True with explicit fields → 'content' appended; original fields kept."""
    api_response = {
        "result": [{"id": 42, "name": "My Node", "content": "some body"}],
        "total": 1,
    }

    captured_request: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_request.append(request)
            return httpx.Response(200, json=api_response)

        mock.get(_NODES_URL).mock(side_effect=capture)
        result = await _call(server, "divoid_list", {"fields": ["id", "name"], "include_content": True})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_request) == 1
    url_params = str(captured_request[0].url)
    for expected_field in ("id", "name", "content"):
        assert expected_field in url_params, (
            f"Expected {expected_field!r} in URL params, got: {url_params!r}"
        )


@pytest.mark.asyncio
async def test_list_include_content_passes_through_binary(server: FastMCP) -> None:
    """Binary content (base64 string + image/png contentType) must pass through verbatim."""
    b64_payload = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
    api_response = {
        "result": [
            {
                "id": 77,
                "type": "documentation",
                "name": "Logo",
                "status": None,
                "contentType": "image/png",
                "content": b64_payload,
            }
        ],
        "total": 1,
    }

    with respx.mock(assert_all_called=False) as mock:
        mock.get(_NODES_URL).mock(return_value=httpx.Response(200, json=api_response))
        result = await _call(server, "divoid_list", {"include_content": True})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    rows = result.get("result", [])
    assert len(rows) == 1
    assert rows[0].get("content") == b64_payload, (
        f"Binary content must pass through verbatim (no decoding), got: {rows[0].get('content')!r}"
    )
    assert rows[0].get("contentType") == "image/png", (
        f"Expected contentType=image/png, got: {rows[0].get('contentType')!r}"
    )


# ---------------------------------------------------------------------------
# divoid_search — include_content tests (DiVoid #1181)
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_search_default_no_content(server: FastMCP) -> None:
    """Default divoid_search must not include content in the result rows."""
    api_response = {
        "result": [
            {
                "id": 55,
                "name": "Auth bug",
                "type": "bug",
                "status": "open",
                "similarity": 0.85,
            }
        ],
        "total": 1,
    }

    with respx.mock(assert_all_called=False) as mock:
        mock.get(_NODES_URL).mock(return_value=httpx.Response(200, json=api_response))
        result = await _call(server, "divoid_search", {"query": "auth bug"})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    rows = result.get("results", [])
    assert len(rows) == 1
    assert "content" not in rows[0], (
        f"Default search must not return 'content' in rows, got: {rows[0]!r}"
    )
    assert "contentType" not in rows[0], (
        f"Default search must not return 'contentType' in rows, got: {rows[0]!r}"
    )


@pytest.mark.asyncio
async def test_search_include_content_passes_through_text(server: FastMCP) -> None:
    """include_content=True → fields param sent, text content passes through."""
    api_response = {
        "result": [
            {
                "id": 190,
                "name": "Hivemind Protocol",
                "type": "documentation",
                "status": None,
                "similarity": 0.93,
                "contentType": "text/markdown",
                "content": "# Hivemind Protocol\n\nStore everything in DiVoid.",
            }
        ],
        "total": 1,
    }

    captured_request: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_request.append(request)
            return httpx.Response(200, json=api_response)

        mock.get(_NODES_URL).mock(side_effect=capture)
        result = await _call(server, "divoid_search", {"query": "hivemind", "include_content": True})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_request) == 1
    url_params = str(captured_request[0].url)
    assert "content" in url_params, (
        f"include_content=True must add 'content' to fields, URL: {url_params!r}"
    )
    rows = result.get("results", [])
    assert len(rows) == 1
    assert rows[0].get("content") == "# Hivemind Protocol\n\nStore everything in DiVoid.", (
        f"Text content must pass through, got: {rows[0].get('content')!r}"
    )
    assert rows[0].get("contentType") == "text/markdown", (
        f"contentType must pass through, got: {rows[0].get('contentType')!r}"
    )
    assert rows[0].get("similarity") == 0.93, (
        f"similarity must still be present, got: {rows[0].get('similarity')!r}"
    )


@pytest.mark.asyncio
async def test_search_include_content_passes_through_binary(server: FastMCP) -> None:
    """include_content=True with binary content → base64 string passes through verbatim."""
    b64_payload = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
    api_response = {
        "result": [
            {
                "id": 200,
                "name": "Diagram",
                "type": "documentation",
                "status": None,
                "similarity": 0.71,
                "contentType": "image/png",
                "content": b64_payload,
            }
        ],
        "total": 1,
    }

    with respx.mock(assert_all_called=False) as mock:
        mock.get(_NODES_URL).mock(return_value=httpx.Response(200, json=api_response))
        result = await _call(server, "divoid_search", {"query": "diagram", "include_content": True})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    rows = result.get("results", [])
    assert len(rows) == 1
    assert rows[0].get("content") == b64_payload, (
        f"Binary content must pass through verbatim, got: {rows[0].get('content')!r}"
    )
    assert rows[0].get("contentType") == "image/png", (
        f"Expected contentType=image/png, got: {rows[0].get('contentType')!r}"
    )


@pytest.mark.asyncio
async def test_search_default_response_shape_unchanged(server: FastMCP) -> None:
    """Regression: default search shape must be exactly id/type/name/status/similarity.

    A future refactor must not accidentally pass through extra backend fields.
    If the backend returns contentType in the default projection, it should still
    be absent from the result when include_content=False and the backend didn't
    send it (which is the default backend behaviour).
    """
    api_response = {
        "result": [
            {
                "id": 314,
                "name": "Tasks",
                "type": None,
                "status": None,
                "similarity": 0.65,
                # No contentType or content — backend default projection omits them.
            }
        ],
        "total": 1,
    }

    with respx.mock(assert_all_called=False) as mock:
        mock.get(_NODES_URL).mock(return_value=httpx.Response(200, json=api_response))
        result = await _call(server, "divoid_search", {"query": "tasks group"})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    rows = result.get("results", [])
    assert len(rows) == 1
    item = rows[0]
    # These five keys must always be present (null values are fine).
    for key in ("id", "type", "name", "status", "similarity"):
        assert key in item, f"Expected key {key!r} in result item, got: {item!r}"
    # These keys must NOT be present unless the backend sent them.
    for key in ("content", "contentType"):
        assert key not in item, (
            f"Key {key!r} must not appear in default-shape result, got: {item!r}"
        )


# ---------------------------------------------------------------------------
# divoid_list — include_links tests (DiVoid #1214)
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_list_default_no_links_in_fields(server: FastMCP) -> None:
    """Default divoid_list call must NOT send fields=links and must NOT return links."""
    api_response = {
        "result": [{"id": 1, "type": "documentation", "name": "Onboarding", "status": None}],
        "total": 1,
    }

    captured_request: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_request.append(request)
            return httpx.Response(200, json=api_response)

        mock.get(_NODES_URL).mock(side_effect=capture)
        result = await _call(server, "divoid_list", {})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_request) == 1
    url_params = str(captured_request[0].url)
    assert "links" not in url_params, (
        f"Default call must not include 'links' in fields, URL: {url_params!r}"
    )
    rows = result.get("result", [])
    assert len(rows) == 1
    assert "links" not in rows[0], (
        f"Default call must not return 'links' in rows, got: {rows[0]!r}"
    )


@pytest.mark.asyncio
async def test_list_include_links_appends_to_default_fields(server: FastMCP) -> None:
    """include_links=True with no explicit fields → fields contains links + defaults."""
    api_response = {
        "result": [
            {
                "id": 9,
                "type": "documentation",
                "name": "Onboarding",
                "status": None,
                "contentType": "text/markdown",
                "links": [3, 7, 190],
            }
        ],
        "total": 1,
    }

    captured_request: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_request.append(request)
            return httpx.Response(200, json=api_response)

        mock.get(_NODES_URL).mock(side_effect=capture)
        result = await _call(server, "divoid_list", {"include_links": True})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_request) == 1
    url_params = str(captured_request[0].url)
    assert "links" in url_params, (
        f"include_links=True must add 'links' to fields, URL: {url_params!r}"
    )
    for expected_field in ("id", "type", "name", "status", "contentType"):
        assert expected_field in url_params, (
            f"Expected default field {expected_field!r} in URL, got: {url_params!r}"
        )
    rows = result.get("result", [])
    assert len(rows) == 1
    assert rows[0].get("links") == [3, 7, 190], (
        f"Expected links to be passed through, got: {rows[0].get('links')!r}"
    )


@pytest.mark.asyncio
async def test_list_include_links_appends_to_explicit_fields(server: FastMCP) -> None:
    """include_links=True with explicit fields → 'links' appended; original fields kept."""
    api_response = {
        "result": [{"id": 42, "name": "My Node", "links": [1, 2]}],
        "total": 1,
    }

    captured_request: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_request.append(request)
            return httpx.Response(200, json=api_response)

        mock.get(_NODES_URL).mock(side_effect=capture)
        result = await _call(server, "divoid_list", {"fields": ["id", "name"], "include_links": True})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_request) == 1
    url_params = str(captured_request[0].url)
    for expected_field in ("id", "name", "links"):
        assert expected_field in url_params, (
            f"Expected {expected_field!r} in URL params, got: {url_params!r}"
        )


@pytest.mark.asyncio
async def test_list_include_links_empty_neighbors(server: FastMCP) -> None:
    """include_links=True with an isolated node (links=[]) passes through unchanged."""
    api_response = {
        "result": [
            {
                "id": 77,
                "type": "documentation",
                "name": "Orphan",
                "status": None,
                "contentType": "text/markdown",
                "links": [],
            }
        ],
        "total": 1,
    }

    with respx.mock(assert_all_called=False) as mock:
        mock.get(_NODES_URL).mock(return_value=httpx.Response(200, json=api_response))
        result = await _call(server, "divoid_list", {"include_links": True})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    rows = result.get("result", [])
    assert len(rows) == 1
    assert rows[0].get("links") == [], (
        f"Empty links array must pass through verbatim, got: {rows[0].get('links')!r}"
    )


@pytest.mark.asyncio
async def test_list_include_content_and_links_together(server: FastMCP) -> None:
    """include_content=True and include_links=True together plumb both into fields."""
    api_response = {
        "result": [
            {
                "id": 190,
                "type": "documentation",
                "name": "Hivemind Protocol",
                "status": None,
                "contentType": "text/markdown",
                "content": "# Hivemind",
                "links": [9, 314],
            }
        ],
        "total": 1,
    }

    captured_request: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_request.append(request)
            return httpx.Response(200, json=api_response)

        mock.get(_NODES_URL).mock(side_effect=capture)
        result = await _call(server, "divoid_list", {"include_content": True, "include_links": True})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_request) == 1
    url_params = str(captured_request[0].url)
    assert "content" in url_params, (
        f"include_content=True must add 'content' to fields, URL: {url_params!r}"
    )
    assert "links" in url_params, (
        f"include_links=True must add 'links' to fields, URL: {url_params!r}"
    )
    rows = result.get("result", [])
    assert len(rows) == 1
    assert rows[0].get("content") == "# Hivemind", (
        f"content must pass through, got: {rows[0].get('content')!r}"
    )
    assert rows[0].get("links") == [9, 314], (
        f"links must pass through, got: {rows[0].get('links')!r}"
    )


# ---------------------------------------------------------------------------
# divoid_search — include_links tests (DiVoid #1214)
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_search_default_no_links(server: FastMCP) -> None:
    """Default divoid_search must not include links in the result rows."""
    api_response = {
        "result": [
            {
                "id": 55,
                "name": "Auth bug",
                "type": "bug",
                "status": "open",
                "similarity": 0.85,
            }
        ],
        "total": 1,
    }

    with respx.mock(assert_all_called=False) as mock:
        mock.get(_NODES_URL).mock(return_value=httpx.Response(200, json=api_response))
        result = await _call(server, "divoid_search", {"query": "auth bug"})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    rows = result.get("results", [])
    assert len(rows) == 1
    assert "links" not in rows[0], (
        f"Default search must not return 'links' in rows, got: {rows[0]!r}"
    )


@pytest.mark.asyncio
async def test_search_include_links_passes_through(server: FastMCP) -> None:
    """include_links=True → fields param sent, links array passes through."""
    api_response = {
        "result": [
            {
                "id": 190,
                "name": "Hivemind Protocol",
                "type": "documentation",
                "status": None,
                "similarity": 0.93,
                "links": [9, 314, 695],
            }
        ],
        "total": 1,
    }

    captured_request: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_request.append(request)
            return httpx.Response(200, json=api_response)

        mock.get(_NODES_URL).mock(side_effect=capture)
        result = await _call(server, "divoid_search", {"query": "hivemind", "include_links": True})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_request) == 1
    url_params = str(captured_request[0].url)
    assert "links" in url_params, (
        f"include_links=True must add 'links' to fields, URL: {url_params!r}"
    )
    rows = result.get("results", [])
    assert len(rows) == 1
    assert rows[0].get("links") == [9, 314, 695], (
        f"links must pass through verbatim, got: {rows[0].get('links')!r}"
    )
    assert rows[0].get("similarity") == 0.93, (
        f"similarity must still be present, got: {rows[0].get('similarity')!r}"
    )


@pytest.mark.asyncio
async def test_search_include_links_empty_neighbors(server: FastMCP) -> None:
    """include_links=True with isolated node (links=[]) passes through unchanged."""
    api_response = {
        "result": [
            {
                "id": 99,
                "name": "Orphan node",
                "type": "documentation",
                "status": None,
                "similarity": 0.60,
                "links": [],
            }
        ],
        "total": 1,
    }

    with respx.mock(assert_all_called=False) as mock:
        mock.get(_NODES_URL).mock(return_value=httpx.Response(200, json=api_response))
        result = await _call(server, "divoid_search", {"query": "orphan", "include_links": True})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    rows = result.get("results", [])
    assert len(rows) == 1
    assert rows[0].get("links") == [], (
        f"Empty links array must pass through verbatim, got: {rows[0].get('links')!r}"
    )


# ---------------------------------------------------------------------------
# divoid_delete_message — 3 hermetic branches
#
# The server returns:
#   204 No Content on success (ok=True, empty body)
#   404 JSON {code, text} when the message does not exist
#   403 JSON {code, text} when the caller is not the recipient and not admin
#
# DiVoid #426 §4: senders cannot recall; only recipient or admin can DELETE.
# The 403 branch is the no-recall guarantee — it is structural, not just policy.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_delete_message_success(server: FastMCP) -> None:
    """204 No Content → tool returns {success: True, id: N} and the DELETE was issued."""
    msg_id = 42
    captured_requests: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_requests.append(request)
            return httpx.Response(204, content=b"")

        mock.delete(_MESSAGES_URL.format(id=msg_id)).mock(side_effect=capture)
        result = await _call(server, "divoid_delete_message", {"id": msg_id})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert result.get("success") is True, f"Expected success=True, got: {result.get('success')!r}"
    assert result.get("id") == msg_id, f"Expected id={msg_id}, got: {result.get('id')!r}"
    # Load-bearing: assert the HTTP DELETE was actually issued.
    assert len(captured_requests) >= 1, (
        "Expected at least one DELETE request to be issued, got 0. "
        "Substitution probe: if this fails after removing the http_client.delete call, "
        "that is the expected failure — the test correctly guards the HTTP behaviour."
    )
    assert captured_requests[0].method == "DELETE", (
        f"Expected DELETE method, got: {captured_requests[0].method!r}"
    )


@pytest.mark.asyncio
async def test_delete_message_not_found(server: FastMCP) -> None:
    """404 → tool returns isError with node_not_found code mapped through shared helper."""
    msg_id = 999_999
    body = json.dumps({
        "code": "data_entitynotfound",
        "text": f"'Message' with id '{msg_id}' not found",
    }).encode()

    with respx.mock(assert_all_called=False) as mock:
        mock.delete(_MESSAGES_URL.format(id=msg_id)).mock(
            return_value=httpx.Response(404, content=body)
        )
        result = await _call(server, "divoid_delete_message", {"id": msg_id})

    assert result.get("isError") is True, f"Expected isError=True for missing message, got: {result}"
    content_text: str = result["content"][0]["text"]
    assert "node_not_found" in content_text, (
        f"Expected 'node_not_found' in error text, got: {content_text!r}"
    )


@pytest.mark.asyncio
async def test_delete_message_forbidden(server: FastMCP) -> None:
    """
    403 → tool returns isError with divoid_bad_request code (4xx fallback).

    Why this branch exists: DiVoid #426 §4 specifies that only the recipient or
    an admin can delete a message. Senders cannot recall — this is the no-recall
    guarantee. The server returns 403 (not 404) for a non-recipient caller so
    that message existence is not leaked to the sender. This test asserts that
    the tool surfaces the 403 as an error rather than masking it as success.
    """
    msg_id = 77
    body = json.dumps({
        "code": "forbidden",
        "text": "only the recipient or an admin can delete this message",
    }).encode()

    with respx.mock(assert_all_called=False) as mock:
        mock.delete(_MESSAGES_URL.format(id=msg_id)).mock(
            return_value=httpx.Response(403, content=body)
        )
        result = await _call(server, "divoid_delete_message", {"id": msg_id})

    assert result.get("isError") is True, f"Expected isError=True for forbidden, got: {result}"
    content_text: str = result["content"][0]["text"]
    # 403 routes through the 4xx fallback in map_http_error → divoid_bad_request.
    assert "divoid_bad_request" in content_text or "403" in content_text, (
        f"Expected 403-mapped error code in text, got: {content_text!r}"
    )


@pytest.mark.asyncio
async def test_list_updated_from_forwarded_as_query_param(server: FastMCP) -> None:
    """updated_from is forwarded to the API as UpdatedFrom without transformation.

    Substitution probe: remove the `params["UpdatedFrom"] = updated_from` line in
    list_nodes.py._execute — this test fails because UpdatedFrom is absent from the URL.
    """
    api_response = {"result": [], "total": 0}
    captured_request: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_request.append(request)
            return httpx.Response(200, json=api_response)

        mock.get(_NODES_URL).mock(side_effect=capture)
        result = await _call(server, "divoid_list", {"updated_from": "2026-05-29T00:00:00Z"})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_request) == 1
    url = str(captured_request[0].url)
    assert "UpdatedFrom=2026-05-29T00%3A00%3A00Z" in url or "UpdatedFrom=2026-05-29T00:00:00Z" in url, (
        f"Expected UpdatedFrom in URL, got: {url!r}. "
        "Substitution probe: removing the UpdatedFrom forwarding line in list_nodes._execute causes this failure."
    )


@pytest.mark.asyncio
async def test_list_created_from_and_updated_to_forwarded(server: FastMCP) -> None:
    """created_from and updated_to are both forwarded independently as CreatedFrom / UpdatedTo."""
    api_response = {"result": [], "total": 0}
    captured_request: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_request.append(request)
            return httpx.Response(200, json=api_response)

        mock.get(_NODES_URL).mock(side_effect=capture)
        result = await _call(server, "divoid_list", {
            "created_from": "2026-01-01T00:00:00Z",
            "updated_to": "2026-06-01T00:00:00Z",
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_request) == 1
    url = str(captured_request[0].url)
    assert "CreatedFrom" in url, f"Expected CreatedFrom in URL, got: {url!r}"
    assert "UpdatedTo" in url, f"Expected UpdatedTo in URL, got: {url!r}"


@pytest.mark.asyncio
async def test_list_no_timestamp_params_when_omitted(server: FastMCP) -> None:
    """Default divoid_list call must NOT send any timestamp params."""
    api_response = {"result": [], "total": 0}
    captured_request: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_request.append(request)
            return httpx.Response(200, json=api_response)

        mock.get(_NODES_URL).mock(side_effect=capture)
        result = await _call(server, "divoid_list", {})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_request) == 1
    url = str(captured_request[0].url)
    for param in ("CreatedFrom", "CreatedTo", "UpdatedFrom", "UpdatedTo"):
        assert param not in url, (
            f"Default call must not include {param!r} in URL, got: {url!r}"
        )


@pytest.mark.asyncio
async def test_search_updated_from_forwarded(server: FastMCP) -> None:
    """updated_from is forwarded to the search API as UpdatedFrom.

    Substitution probe: remove the `params["UpdatedFrom"] = updated_from` line in
    search.py — this test fails because UpdatedFrom is absent from the URL.
    """
    api_response = {"result": [], "total": 0}
    captured_request: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_request.append(request)
            return httpx.Response(200, json=api_response)

        mock.get(_NODES_URL).mock(side_effect=capture)
        result = await _call(server, "divoid_search", {
            "query": "recent changes",
            "updated_from": "2026-05-29T00:00:00Z",
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_request) == 1
    url = str(captured_request[0].url)
    assert "UpdatedFrom" in url, (
        f"Expected UpdatedFrom in URL, got: {url!r}. "
        "Substitution probe: removing the UpdatedFrom forwarding line in search._execute causes this failure."
    )


@pytest.mark.asyncio
async def test_patch_node_access_string_read_write_canonicalized(server: FastMCP) -> None:
    """access="Read, Write" → JSON-Patch op has value=3.

    Substitution probe: remove the access branch from patch_node._execute — the
    /access op is absent from the patch body and this test fails on the assertion
    that the op's value is 3.
    """
    node_id = 42
    captured_body: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_body.append(json.loads(request.content))
            return httpx.Response(200, json={"id": node_id, "name": "test", "access": "Read, Write"})

        mock.patch(_NODE_URL.format(id=node_id)).mock(side_effect=capture)
        result = await _call(server, "divoid_patch_node", {"id": node_id, "access": "Read, Write"})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_body) == 1
    ops = captured_body[0]
    access_ops = [op for op in ops if op.get("path") == "/access"]
    assert len(access_ops) == 1, f"Expected exactly one /access op, got ops: {ops!r}"
    assert access_ops[0]["op"] == "replace", f"Expected op=replace, got: {access_ops[0]!r}"
    assert access_ops[0]["value"] == 3, (
        f"Expected value=3 for 'Read, Write', got: {access_ops[0]['value']!r}. "
        "Substitution probe: removing the access canonicalization line causes this failure."
    )


@pytest.mark.asyncio
async def test_patch_node_access_string_none_canonicalized_to_zero(server: FastMCP) -> None:
    """access="None" → JSON-Patch op has value=0.

    Substitution probe: remove the _canonicalize_access call for "None" → value=0
    assertion fails (would produce a non-zero value or missing op).
    """
    node_id = 55
    captured_body: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_body.append(json.loads(request.content))
            return httpx.Response(200, json={"id": node_id, "name": "private", "access": "None"})

        mock.patch(_NODE_URL.format(id=node_id)).mock(side_effect=capture)
        result = await _call(server, "divoid_patch_node", {"id": node_id, "access": "None"})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    ops = captured_body[0]
    access_ops = [op for op in ops if op.get("path") == "/access"]
    assert len(access_ops) == 1, f"Expected one /access op, got: {ops!r}"
    assert access_ops[0]["value"] == 0, (
        f"Expected value=0 for 'None', got: {access_ops[0]['value']!r}. "
        "Substitution probe: removing _ACCESS_STRING_MAP['None']=0 causes this failure."
    )


@pytest.mark.asyncio
async def test_patch_node_access_int_forwarded_directly(server: FastMCP) -> None:
    """access=1 (integer) → JSON-Patch op has value=1 without transformation."""
    node_id = 77
    captured_body: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_body.append(json.loads(request.content))
            return httpx.Response(200, json={"id": node_id, "name": "readonly", "access": "Read"})

        mock.patch(_NODE_URL.format(id=node_id)).mock(side_effect=capture)
        result = await _call(server, "divoid_patch_node", {"id": node_id, "access": 1})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    ops = captured_body[0]
    access_ops = [op for op in ops if op.get("path") == "/access"]
    assert len(access_ops) == 1, f"Expected one /access op, got: {ops!r}"
    assert access_ops[0]["value"] == 1, f"Expected value=1, got: {access_ops[0]['value']!r}"


@pytest.mark.asyncio
async def test_patch_node_invalid_access_string_returns_error(server: FastMCP) -> None:
    """access='bogus' → invariant guard returns isError before any HTTP call."""
    with respx.mock(assert_all_called=False) as mock:
        mock.patch(_NODE_URL.format(id=1)).mock(return_value=httpx.Response(200, json={}))
        result = await _call(server, "divoid_patch_node", {"id": 1, "access": "bogus"})

    assert result.get("isError") is True, f"Expected isError=True for invalid access string, got: {result}"
    content_text: str = result["content"][0]["text"]
    assert "access_invalid_value" in content_text, (
        f"Expected access_invalid_value error code, got: {content_text!r}"
    )


@pytest.mark.asyncio
async def test_patch_node_owner_id_appends_op(server: FastMCP) -> None:
    """owner_id=99 → JSON-Patch body contains replace /ownerId op with value 99."""
    node_id = 10
    captured_body: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_body.append(json.loads(request.content))
            return httpx.Response(200, json={"id": node_id, "ownerId": 99})

        mock.patch(_NODE_URL.format(id=node_id)).mock(side_effect=capture)
        result = await _call(server, "divoid_patch_node", {"id": node_id, "owner_id": 99})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    ops = captured_body[0]
    owner_ops = [op for op in ops if op.get("path") == "/ownerId"]
    assert len(owner_ops) == 1, f"Expected one /ownerId op, got: {ops!r}"
    assert owner_ops[0]["value"] == 99, f"Expected value=99, got: {owner_ops[0]['value']!r}"


@pytest.mark.asyncio
async def test_get_node_surfaces_access_owner_created_lastupdate(server: FastMCP) -> None:
    """get_node response includes access, ownerId, created, lastUpdate from server.

    Substitution probe: remove the four new keys from get_node.py's return dict —
    access/ownerId/created/lastUpdate are absent from the result and the assertions fail.
    """
    node_id = 9
    server_response = {
        "id": node_id,
        "type": "documentation",
        "name": "Onboarding",
        "status": None,
        "contentType": "text/markdown",
        "x": 0.0,
        "y": 0.0,
        "access": "Read, Write",
        "ownerId": 2,
        "created": "2026-05-01T12:00:00Z",
        "lastUpdate": "2026-05-29T08:30:00Z",
    }

    with respx.mock(assert_all_called=False) as mock:
        mock.get(_NODE_URL.format(id=node_id)).mock(
            return_value=httpx.Response(200, json=server_response)
        )
        result = await _call(server, "divoid_get_node", {"id": node_id})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert result.get("access") == "Read, Write", (
        f"Expected access='Read, Write', got: {result.get('access')!r}. "
        "Substitution probe: removing the 'access' key from get_node return dict causes this failure."
    )
    assert result.get("ownerId") == 2, f"Expected ownerId=2, got: {result.get('ownerId')!r}"
    assert result.get("created") == "2026-05-01T12:00:00Z", (
        f"Expected created='2026-05-01T12:00:00Z', got: {result.get('created')!r}"
    )
    assert result.get("lastUpdate") == "2026-05-29T08:30:00Z", (
        f"Expected lastUpdate='2026-05-29T08:30:00Z', got: {result.get('lastUpdate')!r}"
    )


@pytest.mark.asyncio
async def test_create_task_access_zero_in_post_body(server: FastMCP) -> None:
    """access=0 on divoid_create_task → POST /nodes body carries access=0.

    Substitution probe: remove the `node_body["access"] = _canonicalize_access(access)`
    line from create_task.py — the access key is absent from the POST body and this test fails.
    """
    from divoid_mcp.tools.create_task import register as register_create_task

    ct_server = FastMCP("divoid-mcp-create-test")
    ct_server.config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)  # type: ignore[attr-defined]
    register_create_task(ct_server)

    task_id = 500
    captured_create_body: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture_create(request: httpx.Request) -> httpx.Response:
            captured_create_body.append(json.loads(request.content))
            return httpx.Response(201, json={"id": task_id, "name": "private task", "type": "task"})

        mock.post(_NODES_URL).mock(side_effect=capture_create)
        mock.post(f"{_DUMMY_BASE}/nodes/{task_id}/content").mock(
            return_value=httpx.Response(200, content=b"")
        )
        mock.post(f"{_DUMMY_BASE}/nodes/{task_id}/links").mock(
            return_value=httpx.Response(200, json={})
        )
        mock.get(_NODES_URL).mock(
            return_value=httpx.Response(200, json={"result": [{"id": 314, "name": "Tasks"}], "total": 1})
        )

        result = await _call(ct_server, "divoid_create_task", {
            "name": "private task",
            "tasks_group_id": 314,
            "content": "This task is private.",
            "access": 0,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_create_body) >= 1
    create_body = captured_create_body[0]
    assert "access" in create_body, (
        f"Expected 'access' key in POST body, got: {create_body!r}. "
        "Substitution probe: removing node_body['access'] in create_task.py causes this failure."
    )
    assert create_body["access"] == 0, (
        f"Expected access=0, got: {create_body['access']!r}. "
        "Substitution probe: _canonicalize_access('None'→0) or int(0) branch causes this failure."
    )


@pytest.mark.asyncio
async def test_create_task_access_string_none_canonicalized(server: FastMCP) -> None:
    """access='None' string on divoid_create_task → POST body carries access=0."""
    from divoid_mcp.tools.create_task import register as register_create_task

    ct_server = FastMCP("divoid-mcp-create-test-2")
    ct_server.config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)  # type: ignore[attr-defined]
    register_create_task(ct_server)

    task_id = 501
    captured_create_body: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture_create(request: httpx.Request) -> httpx.Response:
            captured_create_body.append(json.loads(request.content))
            return httpx.Response(201, json={"id": task_id, "name": "secret task", "type": "task"})

        mock.post(_NODES_URL).mock(side_effect=capture_create)
        mock.post(f"{_DUMMY_BASE}/nodes/{task_id}/content").mock(
            return_value=httpx.Response(200, content=b"")
        )
        mock.post(f"{_DUMMY_BASE}/nodes/{task_id}/links").mock(
            return_value=httpx.Response(200, json={})
        )

        result = await _call(ct_server, "divoid_create_task", {
            "name": "secret task",
            "tasks_group_id": 314,
            "content": "Secret task content.",
            "access": "None",
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    create_body = captured_create_body[0]
    assert create_body.get("access") == 0, (
        f"Expected access=0 for string 'None', got: {create_body.get('access')!r}. "
        "Substitution probe: removing _ACCESS_STRING_MAP['None']=0 causes value≠0."
    )


@pytest.mark.asyncio
async def test_get_node_severity_set(server: FastMCP) -> None:
    """get_node returns severity integer when the server sends it.

    Substitution probe: remove the 'severity' key from get_node.py's return dict —
    the key is absent from the result and the assertion fails.
    """
    node_id = 42
    server_response = {
        "id": node_id,
        "type": "task",
        "name": "High priority task",
        "status": "open",
        "severity": 5,
        "contentType": None,
        "x": 0.0,
        "y": 0.0,
        "access": "Read, Write",
        "ownerId": 1,
        "created": "2026-06-01T10:00:00Z",
        "lastUpdate": "2026-06-01T10:00:00Z",
    }

    with respx.mock(assert_all_called=False) as mock:
        mock.get(_NODE_URL.format(id=node_id)).mock(
            return_value=httpx.Response(200, json=server_response)
        )
        result = await _call(server, "divoid_get_node", {"id": node_id})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert "severity" in result, (
        f"Expected 'severity' key in result, got: {list(result.keys())!r}. "
        "Substitution probe: removing 'severity' from get_node return dict causes this failure."
    )
    assert result.get("severity") == 5, (
        f"Expected severity=5, got: {result.get('severity')!r}. "
        "Substitution probe: removing the severity key from the return dict causes this failure."
    )


@pytest.mark.asyncio
async def test_get_node_severity_null(server: FastMCP) -> None:
    """get_node returns severity=None when the server omits it.

    Substitution probe: remove the 'severity' key from get_node.py's return dict —
    the key is absent and the wire-shape regression assertion fails.
    """
    node_id = 99
    server_response = {
        "id": node_id,
        "type": "documentation",
        "name": "Design doc",
        "status": None,
        "contentType": "text/markdown",
        "x": 0.0,
        "y": 0.0,
        "access": "Read, Write",
        "ownerId": 2,
        "created": "2026-06-01T00:00:00Z",
        "lastUpdate": "2026-06-01T00:00:00Z",
    }

    with respx.mock(assert_all_called=False) as mock:
        mock.get(_NODE_URL.format(id=node_id)).mock(
            return_value=httpx.Response(200, json=server_response)
        )
        result = await _call(server, "divoid_get_node", {"id": node_id})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert "severity" in result, (
        f"Expected 'severity' key present even when null, got keys: {list(result.keys())!r}. "
        "The key must always be present so callers can distinguish absent from zero."
    )
    assert result.get("severity") is None, (
        f"Expected severity=None for node without severity, got: {result.get('severity')!r}"
    )


@pytest.mark.asyncio
async def test_list_severity_exact_filter_forwarded(server: FastMCP) -> None:
    """severity=[5] → ?severity=5 appears in the backend URL.

    Substitution probe: remove the severity forwarding block from list_nodes._execute —
    the severity param is absent from the URL and this test fails.
    """
    api_response = {"result": [], "total": 0}
    captured_request: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_request.append(request)
            return httpx.Response(200, json=api_response)

        mock.get(_NODES_URL).mock(side_effect=capture)
        result = await _call(server, "divoid_list", {"severity": [5]})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_request) == 1
    url = str(captured_request[0].url)
    assert "severity=5" in url, (
        f"Expected 'severity=5' in URL, got: {url!r}. "
        "Substitution probe: removing the severity forwarding block in list_nodes._execute causes this failure."
    )


@pytest.mark.asyncio
async def test_list_severity_range_forwarded(server: FastMCP) -> None:
    """severity_min=2, severity_max=4 → ?severityMin=2&severityMax=4 in backend URL.

    Substitution probe: remove the severityMin/severityMax forwarding from _execute —
    either or both params are absent and the assertions fail.
    """
    api_response = {"result": [], "total": 0}
    captured_request: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_request.append(request)
            return httpx.Response(200, json=api_response)

        mock.get(_NODES_URL).mock(side_effect=capture)
        result = await _call(server, "divoid_list", {"severity_min": 2, "severity_max": 4})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_request) == 1
    url = str(captured_request[0].url)
    assert "severityMin=2" in url, (
        f"Expected 'severityMin=2' in URL, got: {url!r}. "
        "Substitution probe: removing severityMin forwarding causes this failure."
    )
    assert "severityMax=4" in url, (
        f"Expected 'severityMax=4' in URL, got: {url!r}. "
        "Substitution probe: removing severityMax forwarding causes this failure."
    )


@pytest.mark.asyncio
async def test_list_no_severity_forwarded(server: FastMCP) -> None:
    """no_severity=True → ?noSeverity=true in backend URL.

    Substitution probe: remove the noSeverity forwarding block from _execute —
    the param is absent from the URL and this test fails.
    """
    api_response = {"result": [], "total": 0}
    captured_request: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_request.append(request)
            return httpx.Response(200, json=api_response)

        mock.get(_NODES_URL).mock(side_effect=capture)
        result = await _call(server, "divoid_list", {"no_severity": True})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_request) == 1
    url = str(captured_request[0].url)
    assert "noSeverity=true" in url, (
        f"Expected 'noSeverity=true' in URL, got: {url!r}. "
        "Substitution probe: removing the noSeverity forwarding block in _execute causes this failure."
    )


@pytest.mark.asyncio
async def test_list_sort_severity_forwarded(server: FastMCP) -> None:
    """sort='severity' → ?sort=severity in backend URL; invariant guard accepts it.

    Substitution probe: remove 'severity' from _VALID_SORT_FIELDS — the invariant
    guard rejects it before any HTTP call and this test fails on isError.
    """
    api_response = {"result": [], "total": 0}
    captured_request: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_request.append(request)
            return httpx.Response(200, json=api_response)

        mock.get(_NODES_URL).mock(side_effect=capture)
        result = await _call(server, "divoid_list", {"sort": "severity"})

    assert result.get("isError") is not True, (
        f"Expected success for sort='severity', got error: {result}. "
        "Substitution probe: removing 'severity' from _VALID_SORT_FIELDS causes invariant rejection."
    )
    assert len(captured_request) == 1
    url = str(captured_request[0].url)
    assert "sort=severity" in url, (
        f"Expected 'sort=severity' in URL, got: {url!r}"
    )


@pytest.mark.asyncio
async def test_list_no_severity_and_severity_mutually_exclusive(server: FastMCP) -> None:
    """no_severity=True and severity=[5] → invariant guard returns isError before HTTP call."""
    with respx.mock(assert_all_called=False) as mock:
        mock.get(_NODES_URL).mock(return_value=httpx.Response(200, json={"result": [], "total": 0}))
        result = await _call(server, "divoid_list", {"no_severity": True, "severity": [5]})

    assert result.get("isError") is True, (
        f"Expected isError=True for no_severity+severity conflict, got: {result}"
    )
    content_text: str = result["content"][0]["text"]
    assert "mutually_exclusive_noseverity_severity" in content_text, (
        f"Expected mutually_exclusive_noseverity_severity error code, got: {content_text!r}"
    )


@pytest.mark.asyncio
async def test_patch_node_severity_value_appends_op(server: FastMCP) -> None:
    """severity=5 → JSON-Patch body contains replace /severity op with value 5.

    Substitution probe: remove the severity block from patch_node._execute — the
    /severity op is absent from the patch body and this test fails.
    """
    node_id = 77
    captured_body: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_body.append(json.loads(request.content))
            return httpx.Response(200, json={"id": node_id, "severity": 5})

        mock.patch(_NODE_URL.format(id=node_id)).mock(side_effect=capture)
        result = await _call(server, "divoid_patch_node", {"id": node_id, "severity": 5})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_body) == 1
    ops = captured_body[0]
    sev_ops = [op for op in ops if op.get("path") == "/severity"]
    assert len(sev_ops) == 1, f"Expected exactly one /severity op, got ops: {ops!r}"
    assert sev_ops[0]["op"] == "replace", f"Expected op=replace, got: {sev_ops[0]!r}"
    assert sev_ops[0]["value"] == 5, (
        f"Expected value=5, got: {sev_ops[0]['value']!r}. "
        "Substitution probe: removing the severity block from _execute causes this failure."
    )


@pytest.mark.asyncio
async def test_patch_node_clear_severity_sends_null(server: FastMCP) -> None:
    """clear_severity=True → JSON-Patch body contains replace /severity op with value null.

    Substitution probe: remove the clear_severity branch from patch_node._execute —
    the /severity op is absent and this test fails.
    """
    node_id = 88
    captured_body: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_body.append(json.loads(request.content))
            return httpx.Response(200, json={"id": node_id, "severity": None})

        mock.patch(_NODE_URL.format(id=node_id)).mock(side_effect=capture)
        result = await _call(server, "divoid_patch_node", {"id": node_id, "clear_severity": True})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_body) == 1
    ops = captured_body[0]
    sev_ops = [op for op in ops if op.get("path") == "/severity"]
    assert len(sev_ops) == 1, f"Expected exactly one /severity op, got ops: {ops!r}"
    assert sev_ops[0]["op"] == "replace", f"Expected op=replace, got: {sev_ops[0]!r}"
    assert sev_ops[0]["value"] is None, (
        f"Expected value=null for clear_severity=True, got: {sev_ops[0]['value']!r}. "
        "Substitution probe: removing the clear_severity branch from _execute causes this failure."
    )


@pytest.mark.asyncio
async def test_create_task_severity_in_post_body(server: FastMCP) -> None:
    """severity=3 on divoid_create_task → POST /nodes body carries severity=3.

    Substitution probe: remove node_body['severity'] = severity from create_task.py —
    the severity key is absent from the POST body and this test fails.
    """
    from divoid_mcp.tools.create_task import register as register_create_task

    ct_server = FastMCP("divoid-mcp-severity-test")
    ct_server.config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)  # type: ignore[attr-defined]
    register_create_task(ct_server)

    task_id = 600
    captured_create_body: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture_create(request: httpx.Request) -> httpx.Response:
            captured_create_body.append(json.loads(request.content))
            return httpx.Response(201, json={"id": task_id, "name": "urgent task", "type": "task"})

        mock.post(_NODES_URL).mock(side_effect=capture_create)
        mock.post(f"{_DUMMY_BASE}/nodes/{task_id}/content").mock(
            return_value=httpx.Response(200, content=b"")
        )
        mock.post(f"{_DUMMY_BASE}/nodes/{task_id}/links").mock(
            return_value=httpx.Response(200, json={})
        )

        result = await _call(ct_server, "divoid_create_task", {
            "name": "urgent task",
            "tasks_group_id": 314,
            "content": "This task has explicit severity.",
            "severity": 3,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_create_body) >= 1
    create_body = captured_create_body[0]
    assert "severity" in create_body, (
        f"Expected 'severity' key in POST body, got: {create_body!r}. "
        "Substitution probe: removing node_body['severity'] in create_task.py causes this failure."
    )
    assert create_body["severity"] == 3, (
        f"Expected severity=3, got: {create_body['severity']!r}."
    )


@pytest.mark.asyncio
async def test_search_severity_in_result_rows(server: FastMCP) -> None:
    """search result rows include severity field when present in API response.

    Substitution probe: remove the severity key from the row construction in search.py —
    severity is absent from the result rows and this test fails.
    """
    api_response = {
        "result": [
            {
                "id": 55,
                "name": "Critical task",
                "type": "task",
                "status": "open",
                "severity": 8,
                "similarity": 0.90,
            }
        ],
        "total": 1,
    }

    with respx.mock(assert_all_called=False) as mock:
        mock.get(_NODES_URL).mock(return_value=httpx.Response(200, json=api_response))
        result = await _call(server, "divoid_search", {"query": "critical task"})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    rows = result.get("results", [])
    assert len(rows) == 1
    assert rows[0].get("severity") == 8, (
        f"Expected severity=8 in search result row, got: {rows[0].get('severity')!r}. "
        "Substitution probe: removing the severity key from search row construction causes this failure."
    )


@pytest.mark.asyncio
async def test_search_severity_null_in_result_rows(server: FastMCP) -> None:
    """search result rows carry severity=None when the node has no severity.

    Substitution probe: remove the severity key from search.py row construction —
    the key is absent and this wire-shape regression assertion fails.
    """
    api_response = {
        "result": [
            {
                "id": 99,
                "name": "No-severity task",
                "type": "task",
                "status": "open",
                "similarity": 0.75,
            }
        ],
        "total": 1,
    }

    with respx.mock(assert_all_called=False) as mock:
        mock.get(_NODES_URL).mock(return_value=httpx.Response(200, json=api_response))
        result = await _call(server, "divoid_search", {"query": "no severity task"})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    rows = result.get("results", [])
    assert len(rows) == 1
    assert "severity" in rows[0], (
        f"Expected 'severity' key present in search result row even when null, "
        f"got keys: {list(rows[0].keys())!r}"
    )
    assert rows[0].get("severity") is None, (
        f"Expected severity=None for node without severity, got: {rows[0].get('severity')!r}"
    )


@pytest.mark.asyncio
async def test_create_documentation_severity_in_post_body(server: FastMCP) -> None:
    """severity=5 on divoid_create_documentation -> POST /nodes body carries severity=5.

    Substitution probe: remove node_body['severity'] = severity from create_documentation.py —
    the severity key is absent from the POST body and this test fails.
    """
    from divoid_mcp.tools.create_documentation import register as register_create_documentation

    doc_server = FastMCP("divoid-mcp-severity-doc-test")
    doc_server.config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)  # type: ignore[attr-defined]
    register_create_documentation(doc_server)

    doc_id = 700
    captured_create_body: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture_create(request: httpx.Request) -> httpx.Response:
            captured_create_body.append(json.loads(request.content))
            return httpx.Response(201, json={"id": doc_id, "name": "severe doc", "type": "documentation"})

        mock.post(_NODES_URL).mock(side_effect=capture_create)
        mock.post(f"{_DUMMY_BASE}/nodes/{doc_id}/content").mock(
            return_value=httpx.Response(200, content=b"")
        )
        mock.post(f"{_DUMMY_BASE}/nodes/{doc_id}/links").mock(
            return_value=httpx.Response(200, json={})
        )

        result = await _call(doc_server, "divoid_create_documentation", {
            "name": "severe doc",
            "docs_group_id": 7,
            "content": "This documentation node has explicit severity.",
            "severity": 5,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_create_body) >= 1
    create_body = captured_create_body[0]
    assert "severity" in create_body, (
        f"Expected 'severity' key in POST body, got: {create_body!r}. "
        "Substitution probe: removing node_body['severity'] in create_documentation.py causes this failure."
    )
    assert create_body["severity"] == 5, (
        f"Expected severity=5, got: {create_body['severity']!r}."
    )


@pytest.mark.asyncio
async def test_create_session_log_severity_in_post_body(server: FastMCP) -> None:
    """severity=7 on divoid_create_session_log -> POST /nodes body carries severity=7.

    Substitution probe: remove node_body['severity'] = severity from create_session_log.py —
    the severity key is absent from the POST body and this test fails.
    """
    from divoid_mcp.tools.create_session_log import register as register_create_session_log

    sl_server = FastMCP("divoid-mcp-severity-sl-test")
    sl_server.config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)  # type: ignore[attr-defined]
    register_create_session_log(sl_server)

    sl_id = 800
    captured_create_body: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture_create(request: httpx.Request) -> httpx.Response:
            captured_create_body.append(json.loads(request.content))
            return httpx.Response(201, json={"id": sl_id, "name": "severity arc log", "type": "session-log"})

        mock.post(_NODES_URL).mock(side_effect=capture_create)
        mock.post(f"{_DUMMY_BASE}/nodes/{sl_id}/content").mock(
            return_value=httpx.Response(200, content=b"")
        )
        mock.post(f"{_DUMMY_BASE}/nodes/{sl_id}/links").mock(
            return_value=httpx.Response(200, json={})
        )

        result = await _call(sl_server, "divoid_create_session_log", {
            "name": "severity arc log",
            "docs_group_id": 7,
            "content": "This session-log node has explicit severity.",
            "severity": 7,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_create_body) >= 1
    create_body = captured_create_body[0]
    assert "severity" in create_body, (
        f"Expected 'severity' key in POST body, got: {create_body!r}. "
        "Substitution probe: removing node_body['severity'] in create_session_log.py causes this failure."
    )
    assert create_body["severity"] == 7, (
        f"Expected severity=7, got: {create_body['severity']!r}."
    )


# ---------------------------------------------------------------------------
# divoid_create_node — invariant guard + happy-path POST body (DiVoid #1364)
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_create_node_empty_name_rejected_before_http(server: FastMCP) -> None:
    """Empty name → invariant guard returns isError before any HTTP call.

    Substitution probe: remove the empty-name check from create_node._check_invariants
    — the guard no longer fires and this test fails because isError is absent.

    Why this test is load-bearing: the name guard is the only hard invariant in the
    generic tool; if it is deleted, callers can POST unnamed nodes to the backend and
    receive opaque server errors instead of a clear MCP error. The test also verifies
    that NO HTTP call is issued when the guard fires — respx.assert_all_called=False
    is intentional; the test explicitly asserts zero POST requests by checking
    captured_requests is empty.
    """
    from divoid_mcp.tools.create_node import register as register_create_node

    cn_server = FastMCP("divoid-mcp-create-node-guard-test")
    cn_server.config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)  # type: ignore[attr-defined]
    register_create_node(cn_server)

    captured_requests: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_requests.append(request)
            return httpx.Response(201, json={"id": 1, "name": ""})

        mock.post(_NODES_URL).mock(side_effect=capture)

        result_empty = await _call(cn_server, "divoid_create_node", {"name": ""})
        result_blank = await _call(cn_server, "divoid_create_node", {"name": "   "})

    assert result_empty.get("isError") is True, (
        f"Expected isError=True for empty name, got: {result_empty}"
    )
    assert "name_required" in result_empty["content"][0]["text"], (
        f"Expected 'name_required' in error text, got: {result_empty['content'][0]['text']!r}. "
        "Substitution probe: removing the name check from _check_invariants causes this failure."
    )
    assert result_blank.get("isError") is True, (
        f"Expected isError=True for blank name, got: {result_blank}"
    )
    assert len(captured_requests) == 0, (
        f"Expected zero HTTP calls when invariant guard fires, got {len(captured_requests)}. "
        "Substitution probe: moving the HTTP call before the guard causes this failure."
    )


@pytest.mark.asyncio
async def test_create_node_meeting_type_post_body_correct(server: FastMCP) -> None:
    """type='meeting' + content + extra_links → POST body has type=meeting, content posted, links created.

    Substitution probe: remove the `if type is not None and type.strip()` branch from
    create_node.py — 'type' is absent from the POST body and this test fails on the
    type assertion. Remove the content POST path — content_length is 0. Remove the
    link loop — extra_links_attached is empty.

    Why this test is load-bearing: it is the primary regression guard for the use-case
    that drove #1364 — creating uncommon node types that the type-specific tools do not
    cover. If any of the three HTTP steps (create, content, link) is removed, at least
    one assertion below fails, so the test cannot pass vacuously.
    """
    from divoid_mcp.tools.create_node import register as register_create_node

    cn_server = FastMCP("divoid-mcp-create-node-meeting-test")
    cn_server.config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)  # type: ignore[attr-defined]
    register_create_node(cn_server)

    meeting_id = 1357
    captured_create_body: list[Any] = []
    content_posted: list[bytes] = []
    link_targets: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture_create(request: httpx.Request) -> httpx.Response:
            captured_create_body.append(json.loads(request.content))
            return httpx.Response(201, json={"id": meeting_id, "name": "2026-06-26 — Theo x Toni", "type": "meeting"})

        def capture_content(request: httpx.Request) -> httpx.Response:
            content_posted.append(request.content)
            return httpx.Response(200, content=b"")

        def capture_link(request: httpx.Request) -> httpx.Response:
            link_targets.append(json.loads(request.content))
            return httpx.Response(200, json={})

        mock.post(_NODES_URL).mock(side_effect=capture_create)
        mock.post(f"{_DUMMY_BASE}/nodes/{meeting_id}/content").mock(side_effect=capture_content)
        mock.post(f"{_DUMMY_BASE}/nodes/{meeting_id}/links").mock(side_effect=capture_link)

        result = await _call(cn_server, "divoid_create_node", {
            "name": "2026-06-26 — Theo x Toni: Planning",
            "type": "meeting",
            "content": "Agenda: Q3 roadmap review. Attendees: Theo, Toni.",
            "extra_links": [499],
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"

    assert len(captured_create_body) == 1, (
        f"Expected exactly 1 POST /nodes call, got {len(captured_create_body)}. "
        "Substitution probe: removing the http_client.post_json('nodes', ...) call causes this failure."
    )
    create_body = captured_create_body[0]
    assert create_body.get("type") == "meeting", (
        f"Expected type='meeting' in POST body, got: {create_body.get('type')!r}. "
        "Substitution probe: removing the type branch in create_node.py causes type to be absent."
    )
    assert create_body.get("name") == "2026-06-26 — Theo x Toni: Planning", (
        f"Expected name in POST body, got: {create_body.get('name')!r}"
    )

    assert len(content_posted) == 1, (
        f"Expected exactly 1 content POST, got {len(content_posted)}. "
        "Substitution probe: removing the content POST path causes content_length=0 "
        "and this assertion fails."
    )
    decoded_content = content_posted[0].decode("utf-8")
    assert "Agenda" in decoded_content, (
        f"Expected content body to be posted, got: {decoded_content!r}"
    )

    assert link_targets == [499], (
        f"Expected extra_links=[499] to be linked, got: {link_targets!r}. "
        "Substitution probe: removing the link loop causes link_targets to be empty."
    )

    assert result.get("id") == meeting_id, f"Expected id={meeting_id}, got: {result.get('id')!r}"
    assert result.get("type") == "meeting", f"Expected type='meeting', got: {result.get('type')!r}"
    assert result.get("extra_links_attached") == [499], (
        f"Expected extra_links_attached=[499], got: {result.get('extra_links_attached')!r}"
    )
    assert isinstance(result.get("content_length"), int) and result["content_length"] > 0, (
        f"Expected positive content_length, got: {result.get('content_length')!r}"
    )


# ---------------------------------------------------------------------------
# rootNodeId — list, search, get_node, create_* (DiVoid #3375)
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_list_root_node_id_forwarded(server: FastMCP) -> None:
    """root_node_id=[42] → ?rootNodeId=42 appears in the backend URL.

    Substitution probe: remove the root_node_id forwarding block from list_nodes._execute —
    the rootNodeId param is absent from the URL and this test fails.
    """
    api_response = {"result": [], "total": 0}
    captured_request: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_request.append(request)
            return httpx.Response(200, json=api_response)

        mock.get(_NODES_URL).mock(side_effect=capture)
        result = await _call(server, "divoid_list", {"root_node_id": [42]})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_request) == 1
    url = str(captured_request[0].url)
    assert "rootNodeId=42" in url, (
        f"Expected 'rootNodeId=42' in URL, got: {url!r}. "
        "Substitution probe: removing the root_node_id forwarding block in list_nodes._execute causes this failure."
    )


@pytest.mark.asyncio
async def test_list_no_root_node_id_forwarded(server: FastMCP) -> None:
    """no_root_node_id=True → ?noRootNodeId=true appears in the backend URL.

    Substitution probe: remove the no_root_node_id forwarding block from _execute —
    the noRootNodeId param is absent from the URL and this test fails.
    """
    api_response = {"result": [], "total": 0}
    captured_request: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_request.append(request)
            return httpx.Response(200, json=api_response)

        mock.get(_NODES_URL).mock(side_effect=capture)
        result = await _call(server, "divoid_list", {"no_root_node_id": True})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_request) == 1
    url = str(captured_request[0].url)
    assert "noRootNodeId=true" in url, (
        f"Expected 'noRootNodeId=true' in URL, got: {url!r}. "
        "Substitution probe: removing the noRootNodeId forwarding block in _execute causes this failure."
    )


@pytest.mark.asyncio
async def test_list_no_root_node_id_and_root_node_id_mutually_exclusive(server: FastMCP) -> None:
    """no_root_node_id=True and root_node_id=[5] → invariant guard returns isError before HTTP call.

    Substitution probe: remove the no_root_node_id/root_node_id mutual-exclusion check from
    _check_invariants — the guard no longer fires and this test fails on isError assertion.
    """
    with respx.mock(assert_all_called=False) as mock:
        mock.get(_NODES_URL).mock(return_value=httpx.Response(200, json={"result": [], "total": 0}))
        result = await _call(server, "divoid_list", {"no_root_node_id": True, "root_node_id": [5]})

    assert result.get("isError") is True, (
        f"Expected isError=True for no_root_node_id+root_node_id conflict, got: {result}"
    )
    content_text: str = result["content"][0]["text"]
    assert "mutually_exclusive_norootnodeid_rootnodeid" in content_text, (
        f"Expected mutually_exclusive_norootnodeid_rootnodeid error code, got: {content_text!r}"
    )


@pytest.mark.asyncio
async def test_search_root_node_id_forwarded(server: FastMCP) -> None:
    """root_node_id=[7] on divoid_search → ?rootNodeId=7 in the backend URL.

    Substitution probe: remove the root_node_id forwarding block from search.py —
    rootNodeId is absent from the URL and this test fails.
    """
    api_response = {"result": [], "total": 0}
    captured_request: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_request.append(request)
            return httpx.Response(200, json=api_response)

        mock.get(_NODES_URL).mock(side_effect=capture)
        result = await _call(server, "divoid_search", {"query": "scoped search", "root_node_id": [7]})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_request) == 1
    url = str(captured_request[0].url)
    assert "rootNodeId=7" in url, (
        f"Expected 'rootNodeId=7' in URL, got: {url!r}. "
        "Substitution probe: removing the root_node_id forwarding block in search.py causes this failure."
    )


@pytest.mark.asyncio
async def test_search_root_node_id_in_result_rows(server: FastMCP) -> None:
    """search result rows include rootNodeId field when present in API response.

    Substitution probe: remove the rootNodeId key from the row construction in search.py —
    rootNodeId is absent from the result rows and this test fails.
    """
    api_response = {
        "result": [
            {
                "id": 55,
                "name": "Grouped doc",
                "type": "documentation",
                "status": None,
                "similarity": 0.88,
                "rootNodeId": 7,
            }
        ],
        "total": 1,
    }

    with respx.mock(assert_all_called=False) as mock:
        mock.get(_NODES_URL).mock(return_value=httpx.Response(200, json=api_response))
        result = await _call(server, "divoid_search", {"query": "grouped doc"})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    rows = result.get("results", [])
    assert len(rows) == 1
    assert rows[0].get("rootNodeId") == 7, (
        f"Expected rootNodeId=7 in search result row, got: {rows[0].get('rootNodeId')!r}. "
        "Substitution probe: removing the rootNodeId key from search row construction causes this failure."
    )


@pytest.mark.asyncio
async def test_search_root_node_id_null_in_result_rows(server: FastMCP) -> None:
    """search result rows carry rootNodeId=None when the node is ungrouped.

    Substitution probe: remove the rootNodeId key from search.py row construction —
    the key is absent and this wire-shape regression assertion fails.
    """
    api_response = {
        "result": [
            {
                "id": 99,
                "name": "Ungrouped doc",
                "type": "documentation",
                "status": None,
                "similarity": 0.75,
            }
        ],
        "total": 1,
    }

    with respx.mock(assert_all_called=False) as mock:
        mock.get(_NODES_URL).mock(return_value=httpx.Response(200, json=api_response))
        result = await _call(server, "divoid_search", {"query": "ungrouped doc"})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    rows = result.get("results", [])
    assert len(rows) == 1
    assert "rootNodeId" in rows[0], (
        f"Expected 'rootNodeId' key present in search result row even when null, "
        f"got keys: {list(rows[0].keys())!r}. "
        "Substitution probe: removing rootNodeId from search row construction causes this failure."
    )
    assert rows[0].get("rootNodeId") is None, (
        f"Expected rootNodeId=None for ungrouped node, got: {rows[0].get('rootNodeId')!r}"
    )


@pytest.mark.asyncio
async def test_get_node_root_node_id_set(server: FastMCP) -> None:
    """get_node returns rootNodeId integer when the server sends it.

    Substitution probe: remove the 'rootNodeId' key from get_node.py's return dict —
    the key is absent from the result and the assertion fails.
    """
    node_id = 55
    server_response = {
        "id": node_id,
        "type": "documentation",
        "name": "Grouped doc",
        "status": None,
        "severity": None,
        "rootNodeId": 7,
        "contentType": "text/markdown",
        "x": 0.0,
        "y": 0.0,
        "access": "Read, Write",
        "ownerId": 2,
        "created": "2026-06-01T10:00:00Z",
        "lastUpdate": "2026-06-01T10:00:00Z",
    }

    with respx.mock(assert_all_called=False) as mock:
        mock.get(_NODE_URL.format(id=node_id)).mock(
            return_value=httpx.Response(200, json=server_response)
        )
        result = await _call(server, "divoid_get_node", {"id": node_id})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert "rootNodeId" in result, (
        f"Expected 'rootNodeId' key in result, got: {list(result.keys())!r}. "
        "Substitution probe: removing 'rootNodeId' from get_node return dict causes this failure."
    )
    assert result.get("rootNodeId") == 7, (
        f"Expected rootNodeId=7, got: {result.get('rootNodeId')!r}. "
        "Substitution probe: removing the rootNodeId key from the return dict causes this failure."
    )


@pytest.mark.asyncio
async def test_get_node_root_node_id_null(server: FastMCP) -> None:
    """get_node returns rootNodeId=None for ungrouped nodes.

    Substitution probe: remove the 'rootNodeId' key from get_node.py's return dict —
    the key is absent and the wire-shape regression assertion fails.
    """
    node_id = 88
    server_response = {
        "id": node_id,
        "type": "documentation",
        "name": "Ungrouped doc",
        "status": None,
        "contentType": "text/markdown",
        "x": 0.0,
        "y": 0.0,
        "access": "Read, Write",
        "ownerId": 2,
        "created": "2026-06-01T00:00:00Z",
        "lastUpdate": "2026-06-01T00:00:00Z",
    }

    with respx.mock(assert_all_called=False) as mock:
        mock.get(_NODE_URL.format(id=node_id)).mock(
            return_value=httpx.Response(200, json=server_response)
        )
        result = await _call(server, "divoid_get_node", {"id": node_id})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert "rootNodeId" in result, (
        f"Expected 'rootNodeId' key present even when null, got keys: {list(result.keys())!r}. "
        "The key must always be present so callers can distinguish absent from ungrouped."
    )
    assert result.get("rootNodeId") is None, (
        f"Expected rootNodeId=None for ungrouped node, got: {result.get('rootNodeId')!r}"
    )


@pytest.mark.asyncio
async def test_create_node_root_node_id_in_post_body(server: FastMCP) -> None:
    """root_node_id=99 on divoid_create_node → POST /nodes body carries rootNodeId=99.

    Substitution probe: remove node_body['rootNodeId'] = root_node_id from create_node.py —
    rootNodeId is absent from the POST body and this test fails.
    """
    from divoid_mcp.tools.create_node import register as register_create_node

    cn_server = FastMCP("divoid-mcp-create-node-rni-test")
    cn_server.config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)  # type: ignore[attr-defined]
    register_create_node(cn_server)

    node_id = 1500
    captured_create_body: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture_create(request: httpx.Request) -> httpx.Response:
            captured_create_body.append(json.loads(request.content))
            return httpx.Response(201, json={"id": node_id, "name": "grouped node", "type": "meeting"})

        mock.post(_NODES_URL).mock(side_effect=capture_create)

        result = await _call(cn_server, "divoid_create_node", {
            "name": "grouped node",
            "type": "meeting",
            "root_node_id": 99,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_create_body) >= 1
    create_body = captured_create_body[0]
    assert "rootNodeId" in create_body, (
        f"Expected 'rootNodeId' key in POST body, got: {create_body!r}. "
        "Substitution probe: removing node_body['rootNodeId'] in create_node.py causes this failure."
    )
    assert create_body["rootNodeId"] == 99, (
        f"Expected rootNodeId=99, got: {create_body['rootNodeId']!r}."
    )
    assert result.get("rootNodeId") == 99, (
        f"Expected rootNodeId=99 in return value, got: {result.get('rootNodeId')!r}. "
        "Substitution probe: removing rootNodeId from the return dict in create_node.py causes this failure."
    )


@pytest.mark.asyncio
async def test_create_task_root_node_id_in_post_body(server: FastMCP) -> None:
    """root_node_id=42 on divoid_create_task → POST /nodes body carries rootNodeId=42.

    Substitution probe: remove node_body['rootNodeId'] = root_node_id from create_task.py —
    rootNodeId is absent from the POST body and this test fails.
    """
    from divoid_mcp.tools.create_task import register as register_create_task

    ct_server = FastMCP("divoid-mcp-create-task-rni-test")
    ct_server.config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)  # type: ignore[attr-defined]
    register_create_task(ct_server)

    task_id = 1600
    captured_create_body: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture_create(request: httpx.Request) -> httpx.Response:
            captured_create_body.append(json.loads(request.content))
            return httpx.Response(201, json={"id": task_id, "name": "grouped task", "type": "task"})

        mock.post(_NODES_URL).mock(side_effect=capture_create)
        mock.post(f"{_DUMMY_BASE}/nodes/{task_id}/content").mock(
            return_value=httpx.Response(200, content=b"")
        )
        mock.post(f"{_DUMMY_BASE}/nodes/{task_id}/links").mock(
            return_value=httpx.Response(200, json={})
        )

        result = await _call(ct_server, "divoid_create_task", {
            "name": "grouped task",
            "tasks_group_id": 314,
            "content": "This task belongs to a root group.",
            "root_node_id": 42,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_create_body) >= 1
    create_body = captured_create_body[0]
    assert "rootNodeId" in create_body, (
        f"Expected 'rootNodeId' key in POST body, got: {create_body!r}. "
        "Substitution probe: removing node_body['rootNodeId'] in create_task.py causes this failure."
    )
    assert create_body["rootNodeId"] == 42, (
        f"Expected rootNodeId=42, got: {create_body['rootNodeId']!r}."
    )


@pytest.mark.asyncio
async def test_create_documentation_root_node_id_in_post_body(server: FastMCP) -> None:
    """root_node_id=7 on divoid_create_documentation → POST /nodes body carries rootNodeId=7.

    Substitution probe: remove node_body['rootNodeId'] = root_node_id from create_documentation.py —
    rootNodeId is absent from the POST body and this test fails.
    """
    from divoid_mcp.tools.create_documentation import register as register_create_documentation

    doc_server = FastMCP("divoid-mcp-create-doc-rni-test")
    doc_server.config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)  # type: ignore[attr-defined]
    register_create_documentation(doc_server)

    doc_id = 1700
    captured_create_body: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture_create(request: httpx.Request) -> httpx.Response:
            captured_create_body.append(json.loads(request.content))
            return httpx.Response(201, json={"id": doc_id, "name": "scoped doc", "type": "documentation"})

        mock.post(_NODES_URL).mock(side_effect=capture_create)
        mock.post(f"{_DUMMY_BASE}/nodes/{doc_id}/content").mock(
            return_value=httpx.Response(200, content=b"")
        )
        mock.post(f"{_DUMMY_BASE}/nodes/{doc_id}/links").mock(
            return_value=httpx.Response(200, json={})
        )

        result = await _call(doc_server, "divoid_create_documentation", {
            "name": "scoped doc",
            "docs_group_id": 7,
            "content": "This doc is scoped to a root group node.",
            "root_node_id": 7,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_create_body) >= 1
    create_body = captured_create_body[0]
    assert "rootNodeId" in create_body, (
        f"Expected 'rootNodeId' key in POST body, got: {create_body!r}. "
        "Substitution probe: removing node_body['rootNodeId'] in create_documentation.py causes this failure."
    )
    assert create_body["rootNodeId"] == 7, (
        f"Expected rootNodeId=7, got: {create_body['rootNodeId']!r}."
    )


@pytest.mark.asyncio
async def test_create_session_log_root_node_id_in_post_body(server: FastMCP) -> None:
    """root_node_id=7 on divoid_create_session_log → POST /nodes body carries rootNodeId=7.

    Substitution probe: remove node_body['rootNodeId'] = root_node_id from create_session_log.py —
    rootNodeId is absent from the POST body and this test fails.
    """
    from divoid_mcp.tools.create_session_log import register as register_create_session_log

    sl_server = FastMCP("divoid-mcp-create-sl-rni-test")
    sl_server.config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)  # type: ignore[attr-defined]
    register_create_session_log(sl_server)

    sl_id = 1800
    captured_create_body: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture_create(request: httpx.Request) -> httpx.Response:
            captured_create_body.append(json.loads(request.content))
            return httpx.Response(201, json={"id": sl_id, "name": "scoped log", "type": "session-log"})

        mock.post(_NODES_URL).mock(side_effect=capture_create)
        mock.post(f"{_DUMMY_BASE}/nodes/{sl_id}/content").mock(
            return_value=httpx.Response(200, content=b"")
        )
        mock.post(f"{_DUMMY_BASE}/nodes/{sl_id}/links").mock(
            return_value=httpx.Response(200, json={})
        )

        result = await _call(sl_server, "divoid_create_session_log", {
            "name": "scoped log",
            "docs_group_id": 7,
            "content": "This session-log is scoped to a root group node.",
            "root_node_id": 7,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_create_body) >= 1
    create_body = captured_create_body[0]
    assert "rootNodeId" in create_body, (
        f"Expected 'rootNodeId' key in POST body, got: {create_body!r}. "
        "Substitution probe: removing node_body['rootNodeId'] in create_session_log.py causes this failure."
    )
    assert create_body["rootNodeId"] == 7, (
        f"Expected rootNodeId=7, got: {create_body['rootNodeId']!r}."
    )
