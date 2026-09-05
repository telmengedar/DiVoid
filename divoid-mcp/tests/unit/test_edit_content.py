"""
Unit tests for divoid_edit_content: translation correctness + invariant guard.

These tests mock the HTTP transport layer (via respx) and assert that:
  1. Each ergonomic verb translates to the correct backend wire shape
     (1-based inclusive → 0-based half-open for lines and chars).
  2. Multiple edits are sent in a single PATCH in the original order.
  3. The "append" verb fetches node content first (one GET), uses len(content)
     as Start (code-point count), and then issues the PATCH.
  4. When no "append" verb is present, no GET is issued — pure arithmetic.
  5. The invariant guard rejects structurally invalid inputs before any HTTP call:
     empty edits, unknown op, non-positive line/char numbers, end < start.

No network calls and no DiVoid credentials are required — respx intercepts every
outbound request. These tests are NOT integration tests; they pin the translation
logic so a bug in _execute cannot slip through the smoke suite undetected.

Architecture reference: DiVoid task #6285, design #6284.
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

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

_DUMMY_BASE = "http://divoid.test"
_DUMMY_KEY = "dummy-key-for-unit-tests"

_CONTENT_URL_TEMPLATE = f"{_DUMMY_BASE}/nodes/{{id}}/content"
_NODE_URL_TEMPLATE = f"{_DUMMY_BASE}/nodes/{{id}}"

# Shared node id used across tests that don't need multiple nodes.
_NODE_ID = 42


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture(scope="module")
def server() -> FastMCP:
    """
    Module-scoped FastMCP server with only divoid_edit_content registered.

    Uses dummy config — no real credentials, no network. http_client is
    initialised with the dummy base URL so respx can intercept all calls.
    """
    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)
    http_client.init(_DUMMY_BASE, _DUMMY_KEY)

    mcp_server = FastMCP("divoid-mcp-edit-content-test")
    mcp_server.config = config  # type: ignore[attr-defined]
    register_edit_content(mcp_server)

    return mcp_server


async def _call(server: FastMCP, args: dict[str, Any]) -> dict[str, Any]:
    """Call divoid_edit_content with the given args and return the raw dict."""
    result = await server._tool_manager.call_tool("divoid_edit_content", args)
    assert isinstance(result, dict), f"Expected dict, got {type(result)}"
    return result


# Minimal success response the mock backend returns.
_OK_NODE = {"id": _NODE_ID, "type": "documentation", "name": "Test node"}


async def _capture_backend_edits(
    server: FastMCP, edits: list[dict[str, Any]], get_content: str | None = None
) -> list[dict[str, Any]]:
    """Calls divoid_edit_content and returns the backend_edits array sent in the PATCH body.

    If get_content is given, also mocks the GET pre-read that "append" issues, returning it.
    """
    captured: list[Any] = []
    with respx.mock(assert_all_called=False) as mock:
        def capture_patch(req: httpx.Request) -> httpx.Response:
            captured.append(json.loads(req.content))
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture_patch)
        if get_content is not None:
            mock.get(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(
                return_value=httpx.Response(
                    200, content=get_content.encode("utf-8"), headers={"content-type": "text/plain"}
                )
            )

        result = await _call(server, {"id": _NODE_ID, "edits": edits})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured) == 1, "Expected exactly one PATCH call"
    return captured[0]


def _apply_line_and_char_edits(content: str, backend_edits: list[dict[str, Any]]) -> str:
    """Mirrors Backend/Models/Nodes/ContentEditor.Apply against a Python str.

    Python str indices already address Unicode code points (unlike C#'s UTF-16 string), so
    char-unit offsets need no surrogate-pair handling here. Used to assert the resulting
    document text for a batch of translated backend edits without a live backend.
    """
    line_offsets = [0]
    for i, ch in enumerate(content):
        if ch == "\n":
            line_offsets.append(i + 1)
    line_offsets.append(len(content))
    char_offsets = list(range(len(content) + 1))

    resolved: list[tuple[int, int, str]] = []
    for edit in backend_edits:
        offsets = line_offsets if edit["unit"] == "line" else char_offsets
        count = len(offsets) - 1
        start, length = edit["start"], edit["length"]
        end = start + length
        if end > count:
            raise ValueError(f"{edit['unit']} range [{start}, {end}) out of bounds; content has {count}")
        resolved.append((offsets[start], offsets[end], edit["value"] or ""))

    order = sorted(range(len(resolved)), key=lambda i: (resolved[i][0], i))

    parts: list[str] = []
    cursor = 0
    previous_end = 0
    for k, i in enumerate(order):
        start, end, value = resolved[i]
        if k > 0 and start < previous_end:
            raise ValueError("content edits overlap")
        parts.append(content[cursor:start])
        parts.append(value)
        cursor = end
        previous_end = end
    parts.append(content[cursor:])
    return "".join(parts)


# ---------------------------------------------------------------------------
# replace_lines translation: 1-based inclusive → 0-based half-open
#
# Human "lines 3–5" (3 lines) → start=2, length=3.
# Substitution probe: if the sl-1 or el-sl+1 arithmetic is wrong,
# the PATCH body will have incorrect start/length and the assertion fails.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_replace_lines_translates_to_0_based_half_open(server: FastMCP) -> None:
    """replace_lines start_line=3, end_line=5 → backend start=2, length=3 (Unit=line).

    Substitution probe: change sl-1 to sl in _execute — start becomes 3 (wrong); test fails.
    """
    captured: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(req: httpx.Request) -> httpx.Response:
            captured.append(json.loads(req.content))
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "replace_lines", "start_line": 3, "end_line": 5, "value": "new\n"}],
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured) == 1, "Expected exactly one PATCH call"
    edits = captured[0]
    assert len(edits) == 1, f"Expected 1 backend edit, got {len(edits)}"
    e = edits[0]
    assert e["unit"] == "line", f"Expected unit='line', got: {e['unit']!r}"
    assert e["start"] == 2, (
        f"Expected start=2 (3-1), got: {e['start']!r}. "
        "Substitution probe: start_line - 1 must convert 1-based to 0-based."
    )
    assert e["length"] == 3, (
        f"Expected length=3 (5-3+1), got: {e['length']!r}. "
        "Substitution probe: end_line - start_line + 1 must span the inclusive range."
    )
    assert e["value"] == "new\n", f"Expected value='new\\n', got: {e['value']!r}"


@pytest.mark.asyncio
async def test_replace_lines_single_line_has_length_one(server: FastMCP) -> None:
    """replace_lines start_line=7, end_line=7 → backend start=6, length=1.

    Substitution probe: change length to end_line - start_line → length=0 (insert, wrong); test fails.
    """
    captured: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(req: httpx.Request) -> httpx.Response:
            captured.append(json.loads(req.content))
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "replace_lines", "start_line": 7, "end_line": 7, "value": "x\n"}],
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    e = captured[0][0]
    assert e["start"] == 6, f"Expected start=6 (7-1), got: {e['start']!r}"
    assert e["length"] == 1, (
        f"Expected length=1 for a single-line replace, got: {e['length']!r}. "
        "Substitution probe: length must be el-sl+1, not el-sl."
    )


@pytest.mark.asyncio
async def test_replace_lines_value_without_newline_gets_terminated(server: FastMCP) -> None:
    """value='X' (no trailing newline) reaches the wire as 'X\\n'."""
    edits = await _capture_backend_edits(server, [
        {"op": "replace_lines", "start_line": 2, "end_line": 2, "value": "X"},
    ])
    assert edits[0]["value"] == "X\n", f"Expected 'X\\n', got: {edits[0]['value']!r}"


@pytest.mark.asyncio
async def test_replace_lines_multiline_value_without_trailing_newline_gets_terminated(server: FastMCP) -> None:
    """A multi-line value only needs its final line terminated, not each internal line."""
    edits = await _capture_backend_edits(server, [
        {"op": "replace_lines", "start_line": 2, "end_line": 3, "value": "A\nB"},
    ])
    assert edits[0]["value"] == "A\nB\n", f"Expected 'A\\nB\\n', got: {edits[0]['value']!r}"


@pytest.mark.asyncio
async def test_replace_lines_value_already_terminated_is_unchanged(server: FastMCP) -> None:
    """Dual: value='X\\n' must NOT become 'X\\n\\n'."""
    edits = await _capture_backend_edits(server, [
        {"op": "replace_lines", "start_line": 2, "end_line": 2, "value": "X\n"},
    ])
    assert edits[0]["value"] == "X\n", f"Expected 'X\\n' unchanged, got: {edits[0]['value']!r}"


@pytest.mark.asyncio
async def test_replace_lines_value_with_double_newline_is_unchanged(server: FastMCP) -> None:
    """Dual: value='X\\n\\n' (caller preserving a following blank line) is left exactly as-is."""
    edits = await _capture_backend_edits(server, [
        {"op": "replace_lines", "start_line": 2, "end_line": 2, "value": "X\n\n"},
    ])
    assert edits[0]["value"] == "X\n\n", f"Expected 'X\\n\\n' unchanged, got: {edits[0]['value']!r}"


@pytest.mark.asyncio
async def test_replace_lines_empty_value_is_not_terminated(server: FastMCP) -> None:
    """Dual: an explicit empty value (the delete-via-replace primitive) must stay empty."""
    edits = await _capture_backend_edits(server, [
        {"op": "replace_lines", "start_line": 2, "end_line": 2, "value": ""},
    ])
    assert edits[0]["value"] == "", f"Expected '' unchanged, got: {edits[0]['value']!r}"


@pytest.mark.asyncio
async def test_replace_lines_crlf_internal_value_gets_crlf_terminated(server: FastMCP) -> None:
    """A value already using CRLF between its own internal lines is terminated with \\r\\n."""
    edits = await _capture_backend_edits(server, [
        {"op": "replace_lines", "start_line": 2, "end_line": 3, "value": "A\r\nB"},
    ])
    assert edits[0]["value"] == "A\r\nB\r\n", f"Expected 'A\\r\\nB\\r\\n', got: {edits[0]['value']!r}"


@pytest.mark.asyncio
async def test_replace_lines_value_already_crlf_terminated_is_unchanged(server: FastMCP) -> None:
    """Dual: value='X\\r\\n' must NOT gain a second terminator."""
    edits = await _capture_backend_edits(server, [
        {"op": "replace_lines", "start_line": 2, "end_line": 2, "value": "X\r\n"},
    ])
    assert edits[0]["value"] == "X\r\n", f"Expected 'X\\r\\n' unchanged, got: {edits[0]['value']!r}"


@pytest.mark.asyncio
async def test_replace_lines_mixed_line_endings_value_defaults_to_lf(server: FastMCP) -> None:
    """Dual: a value mixing \\r\\n and bare \\n internally is ambiguous, so it defaults to \\n."""
    edits = await _capture_backend_edits(server, [
        {"op": "replace_lines", "start_line": 2, "end_line": 4, "value": "A\r\nB\nC"},
    ])
    assert edits[0]["value"] == "A\r\nB\nC\n", f"Expected 'A\\r\\nB\\nC\\n', got: {edits[0]['value']!r}"


# ---------------------------------------------------------------------------
# replace_chars translation: 1-based inclusive → 0-based half-open
#
# Human "chars 5–10" (6 code points) → start=4, length=6.
# Substitution probe: if s-1 or e-s+1 is wrong, start/length will be off.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_replace_chars_translates_to_0_based_half_open(server: FastMCP) -> None:
    """replace_chars start=5, end=10 → backend start=4, length=6 (Unit=char).

    Substitution probe: change s-1 to s in _execute — start becomes 5 (wrong); test fails.
    """
    captured: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(req: httpx.Request) -> httpx.Response:
            captured.append(json.loads(req.content))
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "replace_chars", "start": 5, "end": 10, "value": "hello"}],
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured) == 1
    e = captured[0][0]
    assert e["unit"] == "char", f"Expected unit='char', got: {e['unit']!r}"
    assert e["start"] == 4, (
        f"Expected start=4 (5-1), got: {e['start']!r}. "
        "Substitution probe: start - 1 must convert from 1-based to 0-based."
    )
    assert e["length"] == 6, (
        f"Expected length=6 (10-5+1), got: {e['length']!r}. "
        "Substitution probe: end - start + 1 must span the inclusive range."
    )
    assert e["value"] == "hello", f"Expected value='hello', got: {e['value']!r}"


# ---------------------------------------------------------------------------
# insert_before_line: Length must be 0 (pure insertion, no deletion)
#
# Substitution probe: if length is anything other than 0, it would also
# delete existing content — the test detects this.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_insert_before_line_has_zero_length(server: FastMCP) -> None:
    """insert_before_line line=3 → backend start=2, length=0, Unit=line.

    Substitution probe: set length=1 in _execute for insert_before_line — test fails
    because length != 0 (insert would also delete a line).
    """
    captured: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(req: httpx.Request) -> httpx.Response:
            captured.append(json.loads(req.content))
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "insert_before_line", "line": 3, "value": "inserted\n"}],
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    e = captured[0][0]
    assert e["unit"] == "line", f"Expected unit='line', got: {e['unit']!r}"
    assert e["start"] == 2, f"Expected start=2 (3-1), got: {e['start']!r}"
    assert e["length"] == 0, (
        f"Expected length=0 (pure insertion — no deletion), got: {e['length']!r}. "
        "Substitution probe: insert_before_line must set length=0, not any positive value."
    )
    assert e["value"] == "inserted\n", f"Expected value='inserted\\n', got: {e['value']!r}"


@pytest.mark.asyncio
async def test_insert_before_line_value_without_newline_gets_terminated(server: FastMCP) -> None:
    """value='X' (no trailing newline) reaches the wire as 'X\\n'."""
    edits = await _capture_backend_edits(server, [
        {"op": "insert_before_line", "line": 3, "value": "X"},
    ])
    assert edits[0]["value"] == "X\n", f"Expected 'X\\n', got: {edits[0]['value']!r}"


@pytest.mark.asyncio
async def test_insert_before_line_value_already_terminated_is_unchanged(server: FastMCP) -> None:
    """Dual: value='X\\n' must NOT become 'X\\n\\n'."""
    edits = await _capture_backend_edits(server, [
        {"op": "insert_before_line", "line": 3, "value": "X\n"},
    ])
    assert edits[0]["value"] == "X\n", f"Expected 'X\\n' unchanged, got: {edits[0]['value']!r}"


@pytest.mark.asyncio
async def test_insert_before_line_value_with_double_newline_is_unchanged(server: FastMCP) -> None:
    """Dual: value='X\\n\\n' (inserting a line plus a deliberate blank line) is left as-is."""
    edits = await _capture_backend_edits(server, [
        {"op": "insert_before_line", "line": 3, "value": "X\n\n"},
    ])
    assert edits[0]["value"] == "X\n\n", f"Expected 'X\\n\\n' unchanged, got: {edits[0]['value']!r}"


@pytest.mark.asyncio
async def test_insert_before_line_crlf_internal_value_gets_crlf_terminated(server: FastMCP) -> None:
    """A value already using CRLF between its own internal lines is terminated with \\r\\n."""
    edits = await _capture_backend_edits(server, [
        {"op": "insert_before_line", "line": 3, "value": "A\r\nB"},
    ])
    assert edits[0]["value"] == "A\r\nB\r\n", f"Expected 'A\\r\\nB\\r\\n', got: {edits[0]['value']!r}"


@pytest.mark.asyncio
async def test_insert_before_line_empty_value_is_not_terminated(server: FastMCP) -> None:
    """Dual: an explicit empty value stays a true no-op insertion, not a blank-line insertion."""
    edits = await _capture_backend_edits(server, [
        {"op": "insert_before_line", "line": 3, "value": ""},
    ])
    assert edits[0]["value"] == "", f"Expected '' unchanged, got: {edits[0]['value']!r}"


# ---------------------------------------------------------------------------
# delete_lines: Value must be empty string (no replacement)
#
# Substitution probe: if value passes through a caller-supplied value,
# the delete becomes a replace — the test detects this.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_delete_lines_has_empty_value_and_correct_range(server: FastMCP) -> None:
    """delete_lines start_line=2, end_line=4 → backend start=1, length=3, value=''.

    Substitution probe: pass value through from the edit dict in _execute —
    value would be None/missing instead of '' and the assertion fails.
    """
    captured: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(req: httpx.Request) -> httpx.Response:
            captured.append(json.loads(req.content))
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "delete_lines", "start_line": 2, "end_line": 4}],
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    e = captured[0][0]
    assert e["unit"] == "line", f"Expected unit='line', got: {e['unit']!r}"
    assert e["start"] == 1, f"Expected start=1 (2-1), got: {e['start']!r}"
    assert e["length"] == 3, f"Expected length=3 (4-2+1), got: {e['length']!r}"
    assert e["value"] == "", (
        f"Expected value='' (delete, not replace), got: {e['value']!r}. "
        "Substitution probe: delete_lines must always set value='', ignoring any caller value."
    )


# ---------------------------------------------------------------------------
# append: GET pre-read → Start = char count; PATCH uses that count
#
# Substitution probe: skip the GET call — char_count stays None and the PATCH
# either crashes or sends None as start; both fail the assertion.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_append_fetches_char_count_and_sets_start(server: FastMCP) -> None:
    """append: GET /nodes/{id}/content first → len(body) as Start for PATCH.

    Content is 'hello' (5 ASCII code points) → char_count=5 → PATCH start=5.

    Substitution probe: remove the GET pre-read block in _execute — char_count remains
    None and the backend edit has start=None (or crashes); the assertion on start=5 fails.
    """
    content_bytes = "hello".encode("utf-8")
    captured_patches: list[Any] = []
    captured_gets: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture_get(req: httpx.Request) -> httpx.Response:
            captured_gets.append(req)
            return httpx.Response(200, content=content_bytes, headers={"content-type": "text/plain"})

        def capture_patch(req: httpx.Request) -> httpx.Response:
            captured_patches.append(json.loads(req.content))
            return httpx.Response(200, json=_OK_NODE)

        mock.get(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture_get)
        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture_patch)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "append", "value": " world"}],
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_gets) == 1, (
        f"Expected exactly 1 GET pre-read for append, got {len(captured_gets)}. "
        "Substitution probe: removing the GET pre-read block causes this to be 0."
    )
    assert len(captured_patches) == 1, "Expected exactly 1 PATCH call"
    e = captured_patches[0][0]
    assert e["unit"] == "char", f"Expected unit='char', got: {e['unit']!r}"
    assert e["start"] == 5, (
        f"Expected start=5 (len('hello')), got: {e['start']!r}. "
        "Substitution probe: the GET response body must be decoded and len() used as Start."
    )
    assert e["length"] == 0, f"Expected length=0 (insertion at end), got: {e['length']!r}"
    assert e["value"] == " world", f"Expected value=' world', got: {e['value']!r}"


@pytest.mark.asyncio
async def test_append_unicode_char_count_uses_code_points(server: FastMCP) -> None:
    """append: content 'hello 🌍' has 7 code points (not 10 bytes) → start=7.

    Python str len() counts code points, matching the backend's Char addressing.

    Substitution probe: use len(body_bytes) instead of len(decoded) — start would be
    10 (UTF-8 byte count), not 7; the assertion on start=7 fails.
    """
    # 'hello 🌍' = 7 code points, 10 UTF-8 bytes (earth emoji = 4 bytes)
    content_bytes = "hello \U0001f30d".encode("utf-8")
    assert len(content_bytes) == 10  # bytes
    assert len("hello \U0001f30d") == 7  # code points

    captured_patches: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        mock.get(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(
            return_value=httpx.Response(200, content=content_bytes, headers={"content-type": "text/plain"})
        )

        def capture_patch(req: httpx.Request) -> httpx.Response:
            captured_patches.append(json.loads(req.content))
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture_patch)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "append", "value": "!"}],
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    e = captured_patches[0][0]
    assert e["start"] == 7, (
        f"Expected start=7 (code-point count of 'hello 🌍'), got: {e['start']!r}. "
        "Substitution probe: using byte count (10) instead of code-point count (7) "
        "would give start=10; backend would reject the edit as out-of-bounds."
    )


@pytest.mark.asyncio
async def test_append_value_is_not_line_terminated(server: FastMCP) -> None:
    """Dual against reusing _terminate_line_value for append: value='X' must stay 'X'."""
    edits = await _capture_backend_edits(server, [{"op": "append", "value": "X"}], get_content="hello")
    assert edits[0]["value"] == "X", f"Expected 'X' unchanged, got: {edits[0]['value']!r}"


# ---------------------------------------------------------------------------
# No append → no GET (zero extra round-trips)
#
# Substitution probe: add an unconditional GET pre-read in _execute — this test
# fails because GET is issued even when no append verb is present.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_no_append_no_content_fetch(server: FastMCP) -> None:
    """Two replace_lines edits (no append) → only ONE HTTP call (PATCH), no GET.

    Substitution probe: add an unconditional content GET at the start of _execute —
    the GET URL would be called and this test fails (captured_gets would be non-empty).
    """
    captured_gets: list[httpx.Request] = []
    captured_patches: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture_get(req: httpx.Request) -> httpx.Response:
            captured_gets.append(req)
            return httpx.Response(200, content=b"irrelevant")

        def capture_patch(req: httpx.Request) -> httpx.Response:
            captured_patches.append(json.loads(req.content))
            return httpx.Response(200, json=_OK_NODE)

        mock.get(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture_get)
        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture_patch)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [
                {"op": "replace_lines", "start_line": 1, "end_line": 1, "value": "A\n"},
                {"op": "replace_lines", "start_line": 3, "end_line": 3, "value": "B\n"},
            ],
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_gets) == 0, (
        f"Expected 0 GET calls (no append verb present), got {len(captured_gets)}. "
        "Substitution probe: an unconditional pre-read would trigger a GET here."
    )
    assert len(captured_patches) == 1, f"Expected exactly 1 PATCH call, got {len(captured_patches)}"


# ---------------------------------------------------------------------------
# Multi-edit: array sent in one PATCH, original order preserved
#
# Substitution probe: loop and send one PATCH per edit — len(patches) > 1; test fails.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_multi_edit_sent_as_single_patch_in_order(server: FastMCP) -> None:
    """Three edits of mixed verbs → one PATCH with three items in original order.

    Substitution probe: split edits into individual PATCH calls in _execute —
    len(captured_patches) would be 3, not 1; the assertion fails.
    """
    captured_patches: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(req: httpx.Request) -> httpx.Response:
            captured_patches.append(json.loads(req.content))
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [
                {"op": "replace_lines", "start_line": 1, "end_line": 1, "value": "X\n"},
                {"op": "insert_before_line", "line": 5, "value": "inserted\n"},
                {"op": "delete_lines", "start_line": 8, "end_line": 10},
            ],
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_patches) == 1, (
        f"Expected exactly 1 PATCH call (atomic batch), got {len(captured_patches)}. "
        "Substitution probe: splitting into per-edit PATCHes would give len=3."
    )
    edits = captured_patches[0]
    assert len(edits) == 3, f"Expected 3 edits in the PATCH body, got {len(edits)}"

    # Order must be preserved: replace_lines first, insert_before_line second, delete_lines third.
    assert edits[0]["unit"] == "line" and edits[0]["length"] == 1, (
        f"First edit must be replace_lines (length=1), got: {edits[0]!r}"
    )
    assert edits[1]["unit"] == "line" and edits[1]["length"] == 0, (
        f"Second edit must be insert_before_line (length=0), got: {edits[1]!r}"
    )
    assert edits[2]["unit"] == "line" and edits[2]["value"] == "", (
        f"Third edit must be delete_lines (value=''), got: {edits[2]!r}"
    )


@pytest.mark.asyncio
async def test_append_single_pre_read_shared_by_multiple_appends(server: FastMCP) -> None:
    """Multiple appends in one call → exactly one GET pre-read, one PATCH with both appends.

    All appends in a batch share the same original-frame char count (one pre-read).

    Substitution probe: issue one GET per append in _execute — len(captured_gets)=2; fails.
    """
    content_bytes = b"abc"  # 3 code points
    captured_gets: list[httpx.Request] = []
    captured_patches: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture_get(req: httpx.Request) -> httpx.Response:
            captured_gets.append(req)
            return httpx.Response(200, content=content_bytes, headers={"content-type": "text/plain"})

        def capture_patch(req: httpx.Request) -> httpx.Response:
            captured_patches.append(json.loads(req.content))
            return httpx.Response(200, json=_OK_NODE)

        mock.get(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture_get)
        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture_patch)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [
                {"op": "append", "value": "X"},
                {"op": "append", "value": "Y"},
            ],
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_gets) == 1, (
        f"Expected exactly 1 GET pre-read (shared by both appends), got {len(captured_gets)}. "
        "Substitution probe: one GET per append would give len=2."
    )
    assert len(captured_patches) == 1, "Expected exactly 1 PATCH (atomic batch)"
    edits = captured_patches[0]
    assert len(edits) == 2, f"Expected 2 edits in the PATCH body, got {len(edits)}"
    # Both appends use the same char_count=3 from the single pre-read.
    assert edits[0]["start"] == 3, f"First append must use start=3 (len('abc')), got: {edits[0]['start']!r}"
    assert edits[1]["start"] == 3, f"Second append must use same original-frame start=3, got: {edits[1]['start']!r}"


# ---------------------------------------------------------------------------
# Invariant guard: structural violations rejected BEFORE any HTTP call
#
# Substitution probe for each case: remove the matching check in _check_invariants
# — the HTTP mock would be called (or the wrong violation code returned) and the test fails.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_empty_edits_rejected_before_http(server: FastMCP) -> None:
    """Empty edits list → invariant guard returns isError with 'empty_edits', no HTTP call.

    Substitution probe: remove the `if not edits` check from _check_invariants —
    the PATCH would be called with an empty array and the assertion on isError fails.
    """
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect_http(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect_http)
        mock.get(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect_http)

        result = await _call(server, {"id": _NODE_ID, "edits": []})

    assert result.get("isError") is True, f"Expected isError=True for empty edits, got: {result}"
    assert not http_called, (
        "HTTP must NOT be called when the invariant guard fires. "
        "Substitution probe: removing the empty-edits check lets a PATCH through."
    )
    text = result["content"][0]["text"]
    assert "empty_edits" in text, (
        f"Expected 'empty_edits' code in error text, got: {text!r}. "
        "Substitution probe: wrong code would appear if the wrong guard branch is triggered."
    )


@pytest.mark.asyncio
async def test_unknown_op_rejected_before_http(server: FastMCP) -> None:
    """Unknown op → invariant guard returns isError with 'unknown_op', no HTTP call.

    Substitution probe: remove the `op not in _KNOWN_OPS` check — the edit would
    be silently skipped or cause a KeyError in _execute; both differ from the expected behavior.
    """
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "splice_telepathically", "start_line": 1, "end_line": 1, "value": "x"}],
        })

    assert result.get("isError") is True, f"Expected isError=True for unknown op, got: {result}"
    assert not http_called, "HTTP must NOT be called when op is unknown"
    text = result["content"][0]["text"]
    assert "unknown_op" in text, f"Expected 'unknown_op' code, got: {text!r}"


@pytest.mark.asyncio
async def test_non_positive_line_rejected_before_http(server: FastMCP) -> None:
    """start_line=0 → invariant guard returns isError with 'non_positive_index', no HTTP.

    Line numbers are 1-based; 0 is invalid and the guard must catch it before the PATCH.

    Substitution probe: remove the `sl < 1` check in _check_invariants — the PATCH
    would be sent with start=-1 (0-1), which the backend would reject as 400, but the
    guard is supposed to fire first (this test specifically checks it fires before HTTP).
    """
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(400, json={"code": "error"})

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "replace_lines", "start_line": 0, "end_line": 3, "value": "x"}],
        })

    assert result.get("isError") is True, f"Expected isError=True for line=0, got: {result}"
    assert not http_called, "HTTP must NOT be called — guard must fire before PATCH"
    text = result["content"][0]["text"]
    assert "non_positive_index" in text, f"Expected 'non_positive_index' code, got: {text!r}"


@pytest.mark.asyncio
async def test_end_before_start_rejected_before_http(server: FastMCP) -> None:
    """end_line < start_line → invariant guard returns isError with 'end_before_start'.

    Substitution probe: remove the `el < sl` check — a PATCH with length=-1 would
    be sent, which the backend would reject as 400, but the guard must fire first.
    """
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(400, json={"code": "error"})

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "replace_lines", "start_line": 5, "end_line": 3, "value": "x"}],
        })

    assert result.get("isError") is True, f"Expected isError=True for end<start, got: {result}"
    assert not http_called, "HTTP must NOT be called — guard must fire before PATCH"
    text = result["content"][0]["text"]
    assert "end_before_start" in text, f"Expected 'end_before_start' code, got: {text!r}"


@pytest.mark.asyncio
async def test_end_before_start_replace_chars_rejected(server: FastMCP) -> None:
    """replace_chars end < start → 'end_before_start' before HTTP.

    Substitution probe: remove the `e < s` check in the replace_chars branch —
    the PATCH would fire with a negative length.
    """
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(400, json={"code": "error"})

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "replace_chars", "start": 10, "end": 5, "value": "x"}],
        })

    assert result.get("isError") is True, f"Expected isError=True for chars end<start, got: {result}"
    assert not http_called, "HTTP must NOT be called when end < start"
    text = result["content"][0]["text"]
    assert "end_before_start" in text, f"Expected 'end_before_start' code, got: {text!r}"


@pytest.mark.asyncio
async def test_replace_lines_missing_value_rejected_before_http(server: FastMCP) -> None:
    """replace_lines without value → isError 'missing_field', no HTTP call."""
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "replace_lines", "start_line": 14, "end_line": 14}],
        })

    assert result.get("isError") is True, f"Expected isError=True for missing value, got: {result}"
    assert not http_called, "HTTP must NOT be called when value is missing"
    text = result["content"][0]["text"]
    assert "missing_field" in text, f"Expected 'missing_field' code, got: {text!r}"


@pytest.mark.asyncio
async def test_replace_chars_missing_value_rejected_before_http(server: FastMCP) -> None:
    """replace_chars without value → isError 'missing_field', no HTTP call."""
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "replace_chars", "start": 5, "end": 10}],
        })

    assert result.get("isError") is True, f"Expected isError=True for missing value, got: {result}"
    assert not http_called, "HTTP must NOT be called when value is missing"
    text = result["content"][0]["text"]
    assert "missing_field" in text, f"Expected 'missing_field' code, got: {text!r}"


@pytest.mark.asyncio
async def test_insert_before_line_missing_value_rejected_before_http(server: FastMCP) -> None:
    """insert_before_line without value → isError 'missing_field', no HTTP call.

    This is the exact shape reported in DiVoid #8014: {"op": "insert_before_line", "line": 4}.
    """
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "insert_before_line", "line": 4}],
        })

    assert result.get("isError") is True, f"Expected isError=True for missing value, got: {result}"
    assert not http_called, "HTTP must NOT be called when value is missing"
    text = result["content"][0]["text"]
    assert "missing_field" in text, f"Expected 'missing_field' code, got: {text!r}"


@pytest.mark.asyncio
async def test_append_missing_value_rejected_before_http(server: FastMCP) -> None:
    """append without value → isError 'missing_field', no HTTP call (not even the pre-read GET)."""
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, content=b"irrelevant")

        mock.get(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect)
        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect)

        result = await _call(server, {"id": _NODE_ID, "edits": [{"op": "append"}]})

    assert result.get("isError") is True, f"Expected isError=True for missing value, got: {result}"
    assert not http_called, "HTTP must NOT be called (including pre-read GET) when value is missing"
    text = result["content"][0]["text"]
    assert "missing_field" in text, f"Expected 'missing_field' code, got: {text!r}"


@pytest.mark.asyncio
async def test_delete_lines_without_value_remains_legal(server: FastMCP) -> None:
    """Dual case: delete_lines has no constructive half, so value must NOT be required."""
    captured: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(req: httpx.Request) -> httpx.Response:
            captured.append(json.loads(req.content))
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "delete_lines", "start_line": 2, "end_line": 4}],
        })

    assert result.get("isError") is not True, f"Expected success for delete_lines without value, got: {result}"
    assert len(captured) == 1, "Expected the PATCH to be sent"


@pytest.mark.asyncio
async def test_replace_lines_empty_string_value_is_legal(server: FastMCP) -> None:
    """Dual case: an explicit empty-string value is a legitimate deletion, not a missing field."""
    captured: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(req: httpx.Request) -> httpx.Response:
            captured.append(json.loads(req.content))
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "replace_lines", "start_line": 3, "end_line": 3, "value": ""}],
        })

    assert result.get("isError") is not True, f"Expected success for explicit empty value, got: {result}"
    assert captured[0][0]["value"] == "", f"Expected empty-string value to reach the wire, got: {captured[0][0]!r}"


@pytest.mark.asyncio
async def test_replace_lines_non_string_value_rejected_before_http(server: FastMCP) -> None:
    """replace_lines with value=123 (not a string) → isError 'invalid_field_type', no HTTP call."""
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "replace_lines", "start_line": 1, "end_line": 1, "value": 123}],
        })

    assert result.get("isError") is True, f"Expected isError=True for non-string value, got: {result}"
    assert not http_called, "HTTP must NOT be called when value has the wrong type"
    text = result["content"][0]["text"]
    assert "invalid_field_type" in text, f"Expected 'invalid_field_type' code, got: {text!r}"


@pytest.mark.asyncio
async def test_append_non_string_value_rejected_before_pre_read(server: FastMCP) -> None:
    """append with value=None (not a string) → isError 'invalid_field_type', no pre-read GET."""
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, content=b"irrelevant")

        mock.get(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect)
        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "append", "value": None}],
        })

    assert result.get("isError") is True, f"Expected isError=True for non-string value, got: {result}"
    assert not http_called, "HTTP must NOT be called (including pre-read GET) when value has the wrong type"
    text = result["content"][0]["text"]
    assert "invalid_field_type" in text, f"Expected 'invalid_field_type' code, got: {text!r}"


@pytest.mark.asyncio
async def test_replace_lines_unknown_key_rejected_before_http(server: FastMCP) -> None:
    """replace_lines with an extra 'text' key alongside a valid 'value' → isError 'unknown_field'."""
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "replace_lines", "start_line": 1, "end_line": 1,
                       "value": "ok", "text": "extra"}],
        })

    assert result.get("isError") is True, f"Expected isError=True for unknown key, got: {result}"
    assert not http_called, "HTTP must NOT be called when an edit has an unrecognized key"
    text = result["content"][0]["text"]
    assert "unknown_field" in text, f"Expected 'unknown_field' code, got: {text!r}"
    assert "text" in text, f"Expected the offending key name 'text' in the error, got: {text!r}"


@pytest.mark.asyncio
async def test_replace_chars_unknown_key_rejected_before_http(server: FastMCP) -> None:
    """replace_chars with an extra 'content' key → isError 'unknown_field'."""
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "replace_chars", "start": 1, "end": 2,
                       "value": "ok", "content": "extra"}],
        })

    assert result.get("isError") is True, f"Expected isError=True for unknown key, got: {result}"
    assert not http_called, "HTTP must NOT be called when an edit has an unrecognized key"
    text = result["content"][0]["text"]
    assert "unknown_field" in text, f"Expected 'unknown_field' code, got: {text!r}"
    assert "content" in text, f"Expected the offending key name 'content' in the error, got: {text!r}"


@pytest.mark.asyncio
async def test_insert_before_line_unknown_key_rejected_before_http(server: FastMCP) -> None:
    """insert_before_line with an extra 'text' key → isError 'unknown_field'."""
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "insert_before_line", "line": 3, "value": "ok", "text": "extra"}],
        })

    assert result.get("isError") is True, f"Expected isError=True for unknown key, got: {result}"
    assert not http_called, "HTTP must NOT be called when an edit has an unrecognized key"
    text = result["content"][0]["text"]
    assert "unknown_field" in text, f"Expected 'unknown_field' code, got: {text!r}"


@pytest.mark.asyncio
async def test_delete_lines_unknown_key_rejected_before_http(server: FastMCP) -> None:
    """delete_lines with a 'value' key → isError 'unknown_field'."""
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "delete_lines", "start_line": 1, "end_line": 2, "value": "oops"}],
        })

    assert result.get("isError") is True, f"Expected isError=True for unknown key, got: {result}"
    assert not http_called, "HTTP must NOT be called when an edit has an unrecognized key"
    text = result["content"][0]["text"]
    assert "unknown_field" in text, f"Expected 'unknown_field' code, got: {text!r}"
    assert "value" in text, f"Expected the offending key name 'value' in the error, got: {text!r}"


@pytest.mark.asyncio
async def test_append_unknown_key_rejected_before_http(server: FastMCP) -> None:
    """append with an extra 'content' key → isError 'unknown_field', no pre-read GET."""
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, content=b"irrelevant")

        mock.get(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect)
        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "append", "value": "ok", "content": "extra"}],
        })

    assert result.get("isError") is True, f"Expected isError=True for unknown key, got: {result}"
    assert not http_called, "HTTP must NOT be called (including pre-read GET) when an edit has an unrecognized key"
    text = result["content"][0]["text"]
    assert "unknown_field" in text, f"Expected 'unknown_field' code, got: {text!r}"


@pytest.mark.asyncio
async def test_reported_incident_shape_replace_lines_with_text_key_rejected(server: FastMCP) -> None:
    """The regression test: the exact shape reported in DiVoid #8288 must now raise, not delete."""
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "replace_lines", "start_line": 14, "end_line": 14, "text": "new content"}],
        })

    assert result.get("isError") is True, f"Expected isError=True for the reported incident shape, got: {result}"
    assert not http_called, "HTTP must NOT be called — this is exactly the silent-deletion shape from DiVoid #8288"
    text = result["content"][0]["text"]
    assert "unknown_field" in text, f"Expected 'unknown_field' code, got: {text!r}"


@pytest.mark.asyncio
async def test_e2e_replace_lines_mid_document_no_longer_swallows_next_line(server: FastMCP) -> None:
    """Minimal repro: replace_lines(2,2,'X') on 'L1/L2/L3' must not join L3."""
    content = "L1\nL2\nL3\n"
    edits = await _capture_backend_edits(server, [
        {"op": "replace_lines", "start_line": 2, "end_line": 2, "value": "X"},
    ])
    assert _apply_line_and_char_edits(content, edits) == "L1\nX\nL3\n"


@pytest.mark.asyncio
async def test_e2e_replace_lines_table_header_does_not_swallow_separator_row(server: FastMCP) -> None:
    """Replacing the header row must leave the separator row intact."""
    content = "| A | B |\n|---|---|\n| 1 | 2 |\n"
    edits = await _capture_backend_edits(server, [
        {"op": "replace_lines", "start_line": 1, "end_line": 1, "value": "| Placeholder | Means |"},
    ])
    assert _apply_line_and_char_edits(content, edits) == "| Placeholder | Means |\n|---|---|\n| 1 | 2 |\n"


@pytest.mark.asyncio
async def test_e2e_replace_lines_preserves_intended_single_blank_line(server: FastMCP) -> None:
    """A caller already terminating value with '\\n\\n' keeps exactly one blank line — not zero, not two."""
    content = "L1\nL2\nL3\n"
    edits = await _capture_backend_edits(server, [
        {"op": "replace_lines", "start_line": 2, "end_line": 2, "value": "X\n\n"},
    ])
    assert _apply_line_and_char_edits(content, edits) == "L1\nX\n\nL3\n"


@pytest.mark.asyncio
async def test_e2e_replace_lines_empty_value_deletes_line_cleanly(server: FastMCP) -> None:
    """An explicit empty value removes the line entirely rather than blanking it."""
    content = "L1\nL2\nL3\n"
    edits = await _capture_backend_edits(server, [
        {"op": "replace_lines", "start_line": 2, "end_line": 2, "value": ""},
    ])
    assert _apply_line_and_char_edits(content, edits) == "L1\nL3\n", (
        "If empty values were forced to '\\n', this would be 'L1\\n\\nL3\\n' instead."
    )


@pytest.mark.asyncio
async def test_e2e_replace_lines_value_is_bare_newline_blanks_the_line(server: FastMCP) -> None:
    """value='\\n' (not empty) is a deliberate blank-line replacement, not a deletion."""
    content = "L1\nL2\nL3\n"
    edits = await _capture_backend_edits(server, [
        {"op": "replace_lines", "start_line": 2, "end_line": 2, "value": "\n"},
    ])
    assert _apply_line_and_char_edits(content, edits) == "L1\n\nL3\n"


@pytest.mark.asyncio
async def test_e2e_replace_lines_wholly_empty_document(server: FastMCP) -> None:
    """A wholly empty document has exactly one (empty) addressable line."""
    content = ""
    edits = await _capture_backend_edits(server, [
        {"op": "replace_lines", "start_line": 1, "end_line": 1, "value": "X"},
    ])
    assert _apply_line_and_char_edits(content, edits) == "X\n"


@pytest.mark.asyncio
async def test_e2e_replace_lines_last_line_of_trailing_newline_document(server: FastMCP) -> None:
    """Distinct boundary from the no-trailing-newline case: when the document already ends in
    '\\n', BuildLineOffsets emits a duplicated sentinel for the terminal line, so this exercises
    a different addressed range even though the visible outcome matches."""
    content = "L1\nL2\nL3\n"
    edits = await _capture_backend_edits(server, [
        {"op": "replace_lines", "start_line": 3, "end_line": 3, "value": "X"},
    ])
    assert _apply_line_and_char_edits(content, edits) == "L1\nL2\nX\n"


@pytest.mark.asyncio
async def test_e2e_replace_lines_crlf_multiline_value_preserves_crlf_document(server: FastMCP) -> None:
    """A value already using CRLF between its own internal lines keeps a CRLF document clean."""
    content = "L1\r\nL2\r\nL3\r\n"
    edits = await _capture_backend_edits(server, [
        {"op": "replace_lines", "start_line": 2, "end_line": 3, "value": "A\r\nB"},
    ])
    assert _apply_line_and_char_edits(content, edits) == "L1\r\nA\r\nB\r\n"


@pytest.mark.asyncio
async def test_e2e_replace_lines_crlf_document_with_plain_value_still_mixes_endings(server: FastMCP) -> None:
    """Disclosed residual: a single-line value carries no signal about the document's line-ending
    convention, so replacing one line of a CRLF document with a plain value still mixes endings —
    the same class of trade-off as the last-line case, and unfixable without a pre-read."""
    content = "L1\r\nL2\r\nL3\r\n"
    edits = await _capture_backend_edits(server, [
        {"op": "replace_lines", "start_line": 2, "end_line": 2, "value": "X"},
    ])
    assert _apply_line_and_char_edits(content, edits) == "L1\r\nX\nL3\r\n"


@pytest.mark.asyncio
async def test_e2e_replace_lines_last_line_without_trailing_newline_gains_one(server: FastMCP) -> None:
    """Accepted trade-off: replacing the terminal line of a document that itself has no trailing
    newline gains one, because distinguishing this case would require an unconditional GET before
    every line op, which the architecture (no pre-read unless append is present) rules out."""
    content = "L1\nL2\nL3"
    edits = await _capture_backend_edits(server, [
        {"op": "replace_lines", "start_line": 3, "end_line": 3, "value": "X"},
    ])
    assert _apply_line_and_char_edits(content, edits) == "L1\nL2\nX\n"


@pytest.mark.asyncio
async def test_e2e_replace_lines_single_line_document(server: FastMCP) -> None:
    content = "only line"
    edits = await _capture_backend_edits(server, [
        {"op": "replace_lines", "start_line": 1, "end_line": 1, "value": "X"},
    ])
    assert _apply_line_and_char_edits(content, edits) == "X\n"


@pytest.mark.asyncio
async def test_e2e_insert_before_line_no_longer_merges_with_target_line(server: FastMCP) -> None:
    """insert_before_line's zero-width insertion point sits at the target line's start, so a
    non-terminated value merges with it exactly as directly as replace_lines does."""
    content = "L1\nL2\nL3\n"
    edits = await _capture_backend_edits(server, [
        {"op": "insert_before_line", "line": 2, "value": "NEW"},
    ])
    assert _apply_line_and_char_edits(content, edits) == "L1\nNEW\nL2\nL3\n"


@pytest.mark.asyncio
async def test_e2e_delete_lines_removes_cleanly_no_value_needed(server: FastMCP) -> None:
    """delete_lines is correct by construction: the removed range already includes its own
    terminator, so the surrounding lines close up without any join risk."""
    content = "L1\nL2\nL3\n"
    edits = await _capture_backend_edits(server, [
        {"op": "delete_lines", "start_line": 2, "end_line": 2},
    ])
    assert _apply_line_and_char_edits(content, edits) == "L1\nL3\n"


@pytest.mark.asyncio
async def test_e2e_replace_chars_is_precise_and_line_unaware(server: FastMCP) -> None:
    """replace_chars is correct by construction: codepoint-addressed, so it never touches a
    line boundary and has no analog to the line-unit join bug."""
    content = "L1\nL2\nL3\n"
    edits = await _capture_backend_edits(server, [
        {"op": "replace_chars", "start": 4, "end": 5, "value": "XY"},
    ])
    assert _apply_line_and_char_edits(content, edits) == "L1\nXY\nL3\n"


@pytest.mark.asyncio
async def test_e2e_append_value_is_not_line_terminated(server: FastMCP) -> None:
    """append onto content already ending in '\\n' needs no terminator on its own value to start
    a clean new line — confirming append's char-unit insertion is unaffected by the line-unit fix."""
    content = "L1\nL2\n"
    edits = await _capture_backend_edits(server, [{"op": "append", "value": "L3"}], get_content=content)
    assert _apply_line_and_char_edits(content, edits) == "L1\nL2\nL3"


@pytest.mark.asyncio
async def test_e2e_append_onto_content_without_trailing_newline_merges_unless_value_supplies_one(
    server: FastMCP,
) -> None:
    """append is a pure insertion at the exact end of content, nothing consumed: if content has no
    trailing newline and value has no leading one, they sit on the same line by construction — the
    tool description already shows a leading '\\n' in its append example for exactly this reason."""
    content = "L1\nL2"
    edits = await _capture_backend_edits(server, [{"op": "append", "value": "L3"}], get_content=content)
    assert _apply_line_and_char_edits(content, edits) == "L1\nL2L3"
