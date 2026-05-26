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
from divoid_mcp.tools.delete_message import register as register_delete_message
from divoid_mcp.tools.get_content import register as register_get_content
from divoid_mcp.tools.list_nodes import register as register_list_nodes
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
    register_list_nodes(mcp_server)
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
