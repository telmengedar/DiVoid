"""
Unit tests for divoid_get_content's with_line_numbers mode.

These tests mock the HTTP transport layer (via respx) and assert that:
  1. The default (with_line_numbers omitted/False) path is byte-identical to the
     pre-feature response shape — no line_count key, content unchanged.
  2. Numbering is 1-based and matches Backend/Models/Nodes/ContentEditor.cs's line
     model exactly: no '\\n' at all is one line; a trailing '\\n' yields a final
     empty line; splitting happens on '\\n' only (a CRLF pair is not its own boundary
     and the '\\r' stays attached to the preceding line).
  3. The DiVoid #6341 incident shape (an H1 followed by a blank line) numbers the
     blank line at 2 and the following prose at 3 — the assertion the feature exists
     for.
  4. with_line_numbers is a no-op when the node has no content or non-text content.
  5. A line number taken from the numbered output addresses the exact same text
     divoid_edit_content's real 1-based-inclusive -> 0-based-half-open translation
     would target, for a body combining a trailing newline, a blank line after an H1,
     a CRLF pair, and a multi-byte character.

No network calls and no DiVoid credentials are required; these are not integration
tests. DiVoid refs: #6341, #6948, #8523 §"Defect 3", design #6284.
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
    """with_line_numbers omitted -> content is the raw decoded body, no line_count key.

    Substitution probe: number unconditionally regardless of the flag — the exact-string
    assertion on the raw body fails, and/or 'line_count' appears where it must not.
    """
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
    """Plain 3-line, no-trailing-newline body -> "1\\tfirst" etc, line_count=3.

    Substitution probe: enumerate(lines, start=0) instead of start=1 — first row reads
    "0\\tfirst" instead of "1\\tfirst"; the exact-string assertion fails.
    """
    body = "first\nsecond\nthird"

    with respx.mock(assert_all_called=True) as mock:
        _mock_text_get(mock, body)
        result = await _get(server, {"id": _NODE_ID, "with_line_numbers": True})

    assert result.get("content") == "1\tfirst\n2\tsecond\n3\tthird", f"Got: {result.get('content')!r}"
    assert result.get("line_count") == 3, f"Expected line_count=3, got: {result.get('line_count')!r}"


@pytest.mark.asyncio
async def test_6341_regression_blank_line_after_h1(server: FastMCP) -> None:
    """The exact shape from DiVoid #6341: '# title\\n\\n**What it is:**...' — blank line
    lands at 2, '**What it is:**' at 3. This is the assertion the feature exists for.

    Substitution probe: enumerate(lines, start=0) — blank line would read as line 1 and
    '**What it is:**' as line 2; both assertions fail.
    """
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
    """Content with no '\\n' at all -> exactly one line, per design #6284.

    Substitution probe: unconditionally append a synthetic extra line — line_count
    would read 2 instead of 1.
    """
    body = "onlyline"

    with respx.mock(assert_all_called=True) as mock:
        _mock_text_get(mock, body)
        result = await _get(server, {"id": _NODE_ID, "with_line_numbers": True})

    assert result.get("content") == "1\tonlyline", f"Got: {result.get('content')!r}"
    assert result.get("line_count") == 1, f"Expected line_count=1, got: {result.get('line_count')!r}"


@pytest.mark.asyncio
async def test_trailing_newline_yields_final_empty_line(server: FastMCP) -> None:
    """Content ending in '\\n' -> a final empty line, per design #6284.

    Substitution probe: strip a trailing '\\n' before splitting — the final empty line
    disappears and line_count drops from 2 to 1.
    """
    body = "abc\n"

    with respx.mock(assert_all_called=True) as mock:
        _mock_text_get(mock, body)
        result = await _get(server, {"id": _NODE_ID, "with_line_numbers": True})

    assert result.get("content") == "1\tabc\n2\t", f"Got: {result.get('content')!r}"
    assert result.get("line_count") == 2, f"Expected line_count=2, got: {result.get('line_count')!r}"


@pytest.mark.asyncio
async def test_crlf_preserved_not_treated_as_own_line_boundary(server: FastMCP) -> None:
    """'abc\\r\\ndef' splits only on '\\n' (matching ContentEditor) -> 2 lines, '\\r' stays
    attached to line 1's content.

    Substitution probe: use text.splitlines() instead of text.split('\\n') — splitlines
    treats '\\r\\n' as one boundary AND drops it from the line, so line 1 would read 'abc'
    (no '\\r') instead of 'abc\\r'; the assertion fails.
    """
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
    """Node exists but has no content (404 'has no content') -> unchanged empty-content
    shape, even when with_line_numbers=True. There is no line model for content that
    does not exist (divoid_edit_content itself 404s on this node state).

    Substitution probe: number this branch too (e.g. set line_count=1 unconditionally) —
    the exact-dict-equality assertion fails on the extra key.
    """
    not_found_body = b'{"code":"data_entitynotfound","text":"has no content"}'

    with respx.mock(assert_all_called=True) as mock:
        mock.get(_CONTENT_URL).mock(
            return_value=httpx.Response(404, content=not_found_body, headers={"content-type": "application/json"})
        )
        result = await _get(server, {"id": _NODE_ID, "with_line_numbers": True})

    assert result == {"id": _NODE_ID, "content": "", "content_type": None, "byte_length": 0}, f"Got: {result!r}"


@pytest.mark.asyncio
async def test_with_line_numbers_noop_on_non_text_content(server: FastMCP) -> None:
    """Non-text content-type -> isError, unaffected by with_line_numbers (no numbering
    attempted on binary content).

    Substitution probe: attempt to decode+number before the is_text check — this changes
    the error code or crashes instead of returning content_not_text.
    """
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
    """A body combining a trailing newline, a blank line after an H1 (CRLF-terminated),
    a CRLF pair, and a multi-byte character: hand-verified numbered rows, cross-checked
    against divoid_edit_content's real 1-based-inclusive -> 0-based-half-open wire
    translation for two of those lines.

    body = "# title\\r\\n" + "\\r\\n" + "**Ünïcødé** 🎉 body text\\n"
    Hand split on '\\n' only: ["# title\\r", "\\r", "**Ünïcødé** 🎉 body text", ""]
    -> 4 lines; row 2 is the CRLF blank line (content '\\r'); row 4 is the trailing-
    newline's final empty line.

    Substitution probe: any numbering bug (wrong origin, wrong trailing-newline handling,
    CRLF collapsed via splitlines()) shifts which text a line number addresses — the
    backend slice computed from the real edit_content wire body would then disagree with
    get_content's displayed row for that line.
    """
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
