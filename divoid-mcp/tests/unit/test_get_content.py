"""Unit tests for divoid_get_content's with_line_numbers mode. Mocks the HTTP
transport layer via respx; no network calls or DiVoid credentials required."""

from __future__ import annotations

import json
from typing import Any

import httpx
import pytest
import respx
from mcp.server.fastmcp import FastMCP

from divoid_mcp import http_client
from divoid_mcp.config import DivoidConfig
from divoid_mcp.tools.edit_content import register as register_edit_content
from divoid_mcp.tools.get_content import register as register_get_content

_DUMMY_BASE = "http://divoid.test"
_DUMMY_KEY = "dummy-key-for-unit-tests"

_NODE_ID = 77
_CONTENT_URL = f"{_DUMMY_BASE}/nodes/{_NODE_ID}/content"


@pytest.fixture(scope="module")
def server() -> FastMCP:
    """Module-scoped FastMCP server with divoid_get_content and divoid_edit_content
    registered against a dummy config — no real credentials, no network."""
    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)
    http_client.init(_DUMMY_BASE, _DUMMY_KEY)

    mcp_server = FastMCP("divoid-mcp-get-content-test")
    mcp_server.config = config  # type: ignore[attr-defined]
    register_get_content(mcp_server)
    register_edit_content(mcp_server)

    return mcp_server


async def _get(server: FastMCP, args: dict[str, Any]) -> dict[str, Any]:
    result = await server._tool_manager.call_tool("divoid_get_content", args)
    assert isinstance(result, dict), f"Expected dict, got {type(result)}"
    return result


async def _edit(server: FastMCP, args: dict[str, Any]) -> dict[str, Any]:
    result = await server._tool_manager.call_tool("divoid_edit_content", args)
    assert isinstance(result, dict), f"Expected dict, got {type(result)}"
    return result


def _mock_text_get(mock: respx.MockRouter, body: str, content_type: str = "text/markdown; charset=utf-8") -> None:
    mock.get(_CONTENT_URL).mock(
        return_value=httpx.Response(200, content=body.encode("utf-8"), headers={"content-type": content_type})
    )


def _backend_line_offsets(text: str) -> list[int]:
    """Python replica of ContentEditor.BuildLineOffsets (Backend/Models/Nodes/ContentEditor.cs),
    used only to cross-check get_content's numbering against edit_content's real translation —
    an independent model, not a copy of the production code under test."""
    starts = [0]
    for i, ch in enumerate(text):
        if ch == "\n":
            starts.append(i + 1)
    starts.append(len(text))
    return starts


def _parse_numbered(numbered: str) -> dict[int, str]:
    rows: dict[int, str] = {}
    for row in numbered.split("\n"):
        num_str, _, content = row.partition("\t")
        rows[int(num_str)] = content
    return rows


@pytest.mark.asyncio
async def test_default_path_byte_identical_without_flag(server: FastMCP) -> None:
    """Pins that with_line_numbers omitted returns the raw decoded body with no line_count key."""
    body = "line one\nline two\nline three"

    with respx.mock(assert_all_called=True) as mock:
        _mock_text_get(mock, body)
        result = await _get(server, {"id": _NODE_ID})

    assert result.get("content") == body, f"Expected raw body unchanged, got: {result.get('content')!r}"
    assert "line_count" not in result, (
        "line_count must not appear in the default (with_line_numbers=False) response shape."
    )


@pytest.mark.asyncio
async def test_with_line_numbers_basic_numbering_and_line_count(server: FastMCP) -> None:
    """Pins 1-based numbering and line_count on a plain no-trailing-newline body."""
    body = "first\nsecond\nthird"

    with respx.mock(assert_all_called=True) as mock:
        _mock_text_get(mock, body)
        result = await _get(server, {"id": _NODE_ID, "with_line_numbers": True})

    assert result.get("content") == "1\tfirst\n2\tsecond\n3\tthird", f"Got: {result.get('content')!r}"
    assert result.get("line_count") == 3, f"Expected line_count=3, got: {result.get('line_count')!r}"


@pytest.mark.asyncio
async def test_6341_regression_blank_line_after_h1(server: FastMCP) -> None:
    """Pins the DiVoid #6341 incident shape: blank line after an H1 numbers at 2, not 1."""
    body = "# title\n\n**What it is:** a repo-map node."

    with respx.mock(assert_all_called=True) as mock:
        _mock_text_get(mock, body)
        result = await _get(server, {"id": _NODE_ID, "with_line_numbers": True})

    rows = _parse_numbered(result["content"])
    assert rows[1] == "# title", f"Expected line 1 = '# title', got: {rows.get(1)!r}"
    assert rows[2] == "", f"Expected line 2 = '' (the blank line), got: {rows.get(2)!r}"
    assert rows[3] == "**What it is:** a repo-map node.", (
        f"Expected line 3 = '**What it is:**...', got: {rows.get(3)!r}"
    )


@pytest.mark.asyncio
async def test_no_trailing_newline_is_single_line(server: FastMCP) -> None:
    """Pins that content with no '\\n' at all is exactly one line."""
    body = "onlyline"

    with respx.mock(assert_all_called=True) as mock:
        _mock_text_get(mock, body)
        result = await _get(server, {"id": _NODE_ID, "with_line_numbers": True})

    assert result.get("content") == "1\tonlyline", f"Got: {result.get('content')!r}"
    assert result.get("line_count") == 1, f"Expected line_count=1, got: {result.get('line_count')!r}"


@pytest.mark.asyncio
async def test_trailing_newline_yields_final_empty_line(server: FastMCP) -> None:
    """Pins that a trailing '\\n' produces a final empty line."""
    body = "abc\n"

    with respx.mock(assert_all_called=True) as mock:
        _mock_text_get(mock, body)
        result = await _get(server, {"id": _NODE_ID, "with_line_numbers": True})

    assert result.get("content") == "1\tabc\n2\t", f"Got: {result.get('content')!r}"
    assert result.get("line_count") == 2, f"Expected line_count=2, got: {result.get('line_count')!r}"


@pytest.mark.asyncio
async def test_crlf_preserved_not_treated_as_own_line_boundary(server: FastMCP) -> None:
    """Pins that a CRLF pair is not its own boundary — '\\r' stays attached to the preceding line."""
    body = "abc\r\ndef"

    with respx.mock(assert_all_called=True) as mock:
        _mock_text_get(mock, body)
        result = await _get(server, {"id": _NODE_ID, "with_line_numbers": True})

    rows = _parse_numbered(result["content"])
    assert rows[1] == "abc\r", f"Expected line 1 = 'abc\\r' (CR retained), got: {rows.get(1)!r}"
    assert rows[2] == "def", f"Expected line 2 = 'def', got: {rows.get(2)!r}"
    assert result.get("line_count") == 2, f"Expected line_count=2, got: {result.get('line_count')!r}"


@pytest.mark.asyncio
async def test_with_line_numbers_noop_when_node_has_no_content(server: FastMCP) -> None:
    """Pins that with_line_numbers is a no-op when the node has no content at all."""
    not_found_body = b'{"code":"data_entitynotfound","text":"has no content"}'

    with respx.mock(assert_all_called=True) as mock:
        mock.get(_CONTENT_URL).mock(
            return_value=httpx.Response(404, content=not_found_body, headers={"content-type": "application/json"})
        )
        result = await _get(server, {"id": _NODE_ID, "with_line_numbers": True})

    assert result == {"id": _NODE_ID, "content": "", "content_type": None, "byte_length": 0}, f"Got: {result!r}"


@pytest.mark.asyncio
async def test_with_line_numbers_noop_on_non_text_content(server: FastMCP) -> None:
    """Pins that with_line_numbers is a no-op on non-text content (still isError, unaffected)."""
    with respx.mock(assert_all_called=True) as mock:
        mock.get(_CONTENT_URL).mock(
            return_value=httpx.Response(200, content=b"\x89PNG\r\n", headers={"content-type": "image/png"})
        )
        result = await _get(server, {"id": _NODE_ID, "with_line_numbers": True})

    assert result.get("isError") is True, f"Expected isError=True, got: {result}"
    text = result["content"][0]["text"]
    assert "content_not_text" in text, f"Expected 'content_not_text' code, got: {text!r}"


@pytest.mark.asyncio
async def test_round_trip_against_edit_content_translation(server: FastMCP) -> None:
    """Pins that a line number from the numbered output addresses the same text under
    divoid_edit_content's real wire translation, for a body with a trailing newline, a
    CRLF-terminated blank line after an H1, and a multi-byte character."""
    body = "# title\r\n\r\n**Ünïcødé** 🎉 body text\n"

    with respx.mock(assert_all_called=True) as mock:
        _mock_text_get(mock, body)
        get_result = await _get(server, {"id": _NODE_ID, "with_line_numbers": True})

    rows = _parse_numbered(get_result["content"])
    assert get_result.get("line_count") == 4, f"Expected line_count=4, got: {get_result.get('line_count')!r}"
    assert rows[1] == "# title\r", f"Got: {rows.get(1)!r}"
    assert rows[2] == "\r", f"Got: {rows.get(2)!r}"
    assert rows[3] == "**Ünïcødé** 🎉 body text", f"Got: {rows.get(3)!r}"
    assert rows[4] == "", f"Got: {rows.get(4)!r}"

    offsets = _backend_line_offsets(body)

    for line_no in (3, 4):
        captured: list[Any] = []

        with respx.mock(assert_all_called=False) as edit_mock:
            def capture(req: httpx.Request) -> httpx.Response:
                captured.append(json.loads(req.content))
                return httpx.Response(200, json={"id": _NODE_ID})

            edit_mock.patch(_CONTENT_URL).mock(side_effect=capture)

            edit_result = await _edit(server, {
                "id": _NODE_ID,
                "edits": [{"op": "replace_lines", "start_line": line_no, "end_line": line_no, "value": "X"}],
            })

        assert edit_result.get("isError") is not True, f"Expected success, got: {edit_result}"
        assert len(captured) == 1
        wire = captured[0][0]
        start, length = wire["start"], wire["length"]

        backend_slice = body[offsets[start]:offsets[start + length]]
        expected_display = backend_slice[:-1] if backend_slice.endswith("\n") else backend_slice

        assert rows[line_no] == expected_display, (
            f"Line {line_no}: get_content shows {rows[line_no]!r} but edit_content's real "
            f"wire translation (start={start}, length={length}) addresses {expected_display!r}. "
            "These must be the same text."
        )
